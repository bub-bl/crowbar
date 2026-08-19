namespace Crowbar.Engine.Audio;

/// <summary>
/// Long music decoded in chunks off the DSP thread: a dedicated decoder thread
/// feeds a lock-free SPSC stereo ring buffer, and the voice consumes frames as
/// it goes. Decoding (and any allocation the codecs do) therefore never runs on
/// the DSP thread; <see cref="TryReadFrame"/> only reads the ring and stays
/// allocation-free in steady state.
///
/// Loop is handled internally by the decoder thread
/// (<see cref="LoopsInternally"/> returns <c>true</c>): a looping stream never
/// reports end of stream, so the voice never rewinds it across threads.
/// </summary>
public sealed class AudioStream : IDisposable, IAudioSource
{
    private const int RingShift = 15;   // 32768 frames (~0.68 s @ 48 kHz)
    private const int RingFrames = 1 << RingShift;
    private const int RingMask = RingFrames - 1;
    private const int ChunkFrames = 4096;

    private readonly IAudioDecoder _decoder;
    private readonly float[] _ring = new float[RingFrames * 2];
    private readonly float[] _chunk = new float[ChunkFrames * 2];
    private readonly object _gate = new();

    // Monotonic frame counters, masked into the ring. _produced is written by
    // the decoder thread only; _consumed by the consumer (DSP) thread only.
    // The producer publishes ring data with a Volatile.Write on _produced, and
    // the consumer acquires it with a Volatile.Read before touching the ring.
    private long _produced;
    private long _consumed;
    private int _eof;
    private bool _running;
    private bool _loop;
    private bool _disposed;
    private Thread? _thread;

    public int SampleRate { get; }
    public int Channels => 2;
    public long TotalFrames => _decoder.TotalFrames;

    /// <summary>Known stream duration, or <see cref="TimeSpan.Zero"/> if unknown.</summary>
    public TimeSpan Duration =>
        TotalFrames >= 0 ? TimeSpan.FromSeconds(TotalFrames / (double)SampleRate) : TimeSpan.Zero;

    /// <summary>Loop handled by the decoder thread.</summary>
    public bool Loop
    {
        get => Volatile.Read(ref _loop);
        set => Volatile.Write(ref _loop, value);
    }

    /// <summary>The stream loops by itself; the voice never rewinds it.</summary>
    public bool LoopsInternally => true;

    public AudioStream(string path, bool loop = false)
        : this(AudioDecoderFactory.Create(path), loop)
    {
    }

    public AudioStream(IAudioDecoder decoder, bool loop = false)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        SampleRate = decoder.SampleRate;

        // Set before starting the decoder thread: after the thread has stopped
        // at end of stream, changing Loop no longer has an effect (use Reset).
        _loop = loop;

        // Prime enough frames synchronously so the first block never underruns.
        Prime();
        StartThread();
    }

    public bool TryReadFrame(out float left, out float right)
    {
        var consumed = _consumed;
        var produced = Volatile.Read(ref _produced);

        if (consumed < produced)
        {
            ReadRing(consumed, out left, out right);
            Volatile.Write(ref _consumed, consumed + 1);
            return true;
        }

        if (Volatile.Read(ref _eof) != 0)
        {
            left = 0f;
            right = 0f;
            return false;
        }

        // The ring is drained but the decoder is still running: wait briefly
        // for the next chunk instead of declaring end of stream. This only
        // happens when the consumer temporarily outruns the decoder (or in the
        // instant before the producer thread starts); the prime above makes it
        // a rare, sub-millisecond wait.
        var spin = new SpinWait();
        for (var i = 0; i < 2000; i++)
        {
            spin.SpinOnce();
            produced = Volatile.Read(ref _produced);
            if (consumed < produced)
            {
                ReadRing(consumed, out left, out right);
                Volatile.Write(ref _consumed, consumed + 1);
                return true;
            }

            if (Volatile.Read(ref _eof) != 0)
                break;
        }

        // The producer is genuinely stalled: emit silence without ending the
        // voice; the next block will retry.
        left = 0f;
        right = 0f;
        return false;
    }

    /// <summary>
    /// Rewinds playback to the start. Not for use on a live-playing stream: it
    /// stops and restarts the decoder thread, so it should be called between
    /// plays (from the game thread), never from the DSP thread.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            StopThread();
            _decoder.Seek(0);
            _produced = 0;
            _consumed = 0;
            Volatile.Write(ref _eof, 0);
            Prime();
            StartThread();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            StopThread();
            _decoder.Dispose();
        }
    }

    private void StartThread()
    {
        Volatile.Write(ref _running, true);
        _thread = new Thread(ProducerLoop)
        {
            Name = "Crowbar.Audio.Stream",
            IsBackground = true
        };
        _thread.Start();
    }

    private void StopThread()
    {
        Volatile.Write(ref _running, false);
        var thread = _thread;
        if (thread is not null)
        {
            thread.Join();
            _thread = null;
        }
    }

    private void Prime()
    {
        var frames = _decoder.Read(_chunk.AsSpan(0, ChunkFrames * 2));
        if (frames <= 0)
            return;

        WriteChunk(_produced, frames);
        _produced += frames;
        Volatile.Write(ref _produced, _produced);
    }

    private void ProducerLoop()
    {
        while (Volatile.Read(ref _running))
        {
            var free = RingFrames - (int)(_produced - Volatile.Read(ref _consumed));
            if (free <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            var want = Math.Min(ChunkFrames, free);
            var frames = _decoder.Read(_chunk.AsSpan(0, want * 2));
            if (frames <= 0)
            {
                if (Volatile.Read(ref _loop))
                {
                    _decoder.Seek(0);
                    continue;
                }

                Volatile.Write(ref _eof, 1);
                return;
            }

            WriteChunk(_produced, frames);
            _produced += frames;
            Volatile.Write(ref _produced, _produced);
        }
    }

    private void WriteChunk(long produced, int frames)
    {
        var start = (int)(produced & RingMask);
        var first = Math.Min(frames, RingFrames - start);

        for (var i = 0; i < first; i++)
        {
            _ring[(start + i) * 2] = _chunk[i * 2];
            _ring[(start + i) * 2 + 1] = _chunk[i * 2 + 1];
        }

        var rest = frames - first;
        for (var i = 0; i < rest; i++)
        {
            _ring[i * 2] = _chunk[(first + i) * 2];
            _ring[i * 2 + 1] = _chunk[(first + i) * 2 + 1];
        }
    }

    private void ReadRing(long consumed, out float left, out float right)
    {
        var index = (int)(consumed & RingMask);
        left = _ring[index * 2];
        right = _ring[index * 2 + 1];
    }
}
