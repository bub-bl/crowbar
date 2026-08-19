namespace Crowbar.Engine.Audio;

/// <summary>
/// In-memory microphone input buffer: a capture thread drains the device and
/// fills a stereo float ring buffer. The game can then either <see cref="Read"/>
/// (consume as a stream, for real-time processing) or <see cref="TakeClip"/> (a
/// snapshot of the last moments, to replay via
/// <see cref="Audio.Play(AudioClip, float, float, float, bool, float, int, AudioBusName)"/>).
///
/// Complementary to <see cref="AudioRecorder.StartInput"/>: here nothing is
/// written to disk, the samples stay available on the CPU side.
/// </summary>
public sealed class Microphone : IDisposable
{
    private readonly object _gate = new();
    private readonly float[] _scratch = new float[AudioSystem.BlockSize * 2];
    private readonly IAudioBackend? _backend;

    private float[] _buffer;
    private int _capacityFrames;
    private int _writeIndex;
    private int _available;
    private long _totalFrames;
    private IAudioCaptureDevice? _capture;
    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    public int SampleRate { get; private set; } = AudioSystem.SampleRate;

    /// <summary>The engine always normalizes to interleaved stereo.</summary>
    public int Channels => 2;

    /// <summary>True while capture is running.</summary>
    public bool IsActive => _running;

    /// <summary>Ring buffer capacity, in frames.</summary>
    public int CapacityFrames
    {
        get
        {
            lock (_gate)
                return _capacityFrames;
        }
    }

    /// <summary>Captured frames available to read (not yet consumed).</summary>
    public int AvailableFrames
    {
        get
        {
            lock (_gate)
                return _available;
        }
    }

    /// <summary>Total number of frames captured since startup.</summary>
    public long TotalFrames
    {
        get
        {
            lock (_gate)
                return _totalFrames;
        }
    }

    internal Microphone(IAudioBackend? backend)
    {
        _backend = backend;
        // A default one-second buffer; Start can resize it.
        _capacityFrames = AudioSystem.SampleRate;
        _buffer = new float[_capacityFrames * 2];
    }

    /// <summary>
    /// Opens the capture device and starts the thread that fills the ring
    /// buffer. Returns false if no backend or device is available.
    /// </summary>
    public bool Start(string? device = null, float bufferSeconds = 5f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running || _backend is null)
            return false;

        var capture = _backend.OpenCapture(device);
        if (capture is null)
            return false;

        _capture = capture;
        SampleRate = capture.SampleRate;
        Resize(Math.Clamp(bufferSeconds, 0.1f, 60f));
        capture.Start();

        lock (_gate)
        {
            _writeIndex = 0;
            _available = 0;
            _totalFrames = 0;
        }

        _running = true;
        _thread = new Thread(CaptureLoop)
        {
            Name = "Crowbar.Audio.Microphone",
            IsBackground = true
        };
        _thread.Start();
        return true;
    }

    /// <summary>Stops capture and releases the device (the buffer stays readable).</summary>
    public void Stop()
    {
        if (!_running)
            return;

        _running = false;
        _thread?.Join(2000);
        _thread = null;
        _capture?.Dispose();
        _capture = null;
    }

    /// <summary>
    /// Consumes up to <c>destination.Length / 2</c> frames (the oldest ones) and
    /// returns the number of frames read.
    /// </summary>
    public int Read(Span<float> destination)
    {
        var frames = destination.Length / 2;
        lock (_gate)
        {
            var count = Math.Min(frames, _available);
            var start = ReadStart();
            for (var i = 0; i < count; i++)
            {
                var index = (start + i) % _capacityFrames;
                destination[i * 2] = _buffer[index * 2];
                destination[i * 2 + 1] = _buffer[index * 2 + 1];
            }
            _available -= count;
            return count;
        }
    }

    /// <summary>
    /// Non-destructive snapshot of the last captured moments, ready to be
    /// replayed. <paramref name="seconds"/> is 0 for the whole buffer; otherwise
    /// the clip holds at most <paramref name="seconds"/> seconds (the most
    /// recent ones).
    /// </summary>
    public AudioClip TakeClip(float seconds = 0f)
    {
        var frames = 0;
        lock (_gate)
        {
            frames = seconds > 0f
                ? Math.Min(_available, (int)(seconds * SampleRate))
                : _available;
        }

        if (frames <= 0)
            return AudioClip.Create("Microphone", SampleRate, ReadOnlySpan<float>.Empty, 2);

        var data = new float[frames * 2];
        lock (_gate)
        {
            var start = (ReadStart() + (_available - frames)) % _capacityFrames;
            for (var i = 0; i < frames; i++)
            {
                var index = (start + i) % _capacityFrames;
                data[i * 2] = _buffer[index * 2];
                data[i * 2 + 1] = _buffer[index * 2 + 1];
            }
        }

        return AudioClip.Create("Microphone", SampleRate, data, 2);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    /// <summary>Writes stereo frames into the ring buffer (capture thread, or test).</summary>
    internal void Push(ReadOnlySpan<float> stereo)
    {
        var frames = stereo.Length / 2;
        if (frames == 0)
            return;

        lock (_gate)
        {
            if (_capacityFrames == 0)
                return;

            for (var i = 0; i < frames; i++)
            {
                _buffer[_writeIndex * 2] = stereo[i * 2];
                _buffer[_writeIndex * 2 + 1] = stereo[i * 2 + 1];
                _writeIndex = (_writeIndex + 1) % _capacityFrames;
            }

            _available = Math.Min(_available + frames, _capacityFrames);
            _totalFrames += frames;
        }
    }

    private void CaptureLoop()
    {
        var capture = _capture;
        if (capture is null)
            return;

        while (_running)
        {
            var read = capture.Read(_scratch);
            if (read > 0)
                Push(_scratch.AsSpan(0, read * 2));
            else
                Thread.Sleep(1);
        }
    }

    private int ReadStart()
    {
        // Oldest frame still available (the ring buffer overwrites
        // consumed/older frames).
        var offset = _writeIndex - _available;
        return offset >= 0 ? offset : offset + _capacityFrames;
    }

    private void Resize(float seconds)
    {
        var frames = Math.Max(1, (int)(seconds * SampleRate));
        lock (_gate)
        {
            if (_capacityFrames == frames)
                return;

            _buffer = new float[frames * 2];
            _capacityFrames = frames;
            _writeIndex = 0;
            _available = 0;
            _totalFrames = 0;
        }
    }
}
