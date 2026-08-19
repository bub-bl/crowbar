namespace Crowbar.Engine.Audio;

/// <summary>
/// Long music decoded in chunks: an <see cref="IAudioDecoder"/> feeds a stereo
/// ring buffer, and the voice consumes the frames as it goes. The file is loaded
/// once and decoding happens in place, without allocation on the DSP thread in
/// steady state.
///
/// Playback is always stereo (the decoder already normalizes the channels).
/// </summary>
public sealed class AudioStream : IDisposable, IAudioSource
{
    private readonly IAudioDecoder _decoder;
    private readonly float[] _ring;
    private readonly float[] _chunk;
    private readonly int _ringFrames;
    private int _readIndex;
    private int _writeIndex;
    private int _available;

    public int SampleRate { get; }
    public int Channels => 2;
    public long TotalFrames => _decoder.TotalFrames;

    /// <summary>Known stream duration, or <see cref="TimeSpan.Zero"/> if unknown.</summary>
    public TimeSpan Duration =>
        TotalFrames >= 0 ? TimeSpan.FromSeconds(TotalFrames / (double)SampleRate) : TimeSpan.Zero;

    public AudioStream(string path)
        : this(AudioDecoderFactory.Create(path))
    {
    }

    public AudioStream(IAudioDecoder decoder)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        SampleRate = decoder.SampleRate;
        // ~0.68 s of buffer, stereo.
        _ringFrames = 1 << 15;
        _ring = new float[_ringFrames * 2];
        _chunk = new float[AudioSystem.BlockSize * 2];
    }

    public void Reset()
    {
        _decoder.Seek(0);
        _readIndex = 0;
        _writeIndex = 0;
        _available = 0;
    }

    public bool TryReadFrame(out float left, out float right)
    {
        if (_available == 0 && !Refill())
        {
            left = 0f;
            right = 0f;
            return false;
        }

        left = _ring[_readIndex * 2];
        right = _ring[_readIndex * 2 + 1];
        _readIndex = (_readIndex + 1) % _ringFrames;
        _available--;
        return true;
    }

    public void Dispose() => _decoder.Dispose();

    private bool Refill()
    {
        var frames = _decoder.Read(_chunk);
        if (frames <= 0)
            return false;

        var count = frames;
        for (var i = 0; i < count; i++)
        {
            _ring[_writeIndex * 2] = _chunk[i * 2];
            _ring[_writeIndex * 2 + 1] = _chunk[i * 2 + 1];
            _writeIndex = (_writeIndex + 1) % _ringFrames;
        }

        _available += count;
        return true;
    }
}
