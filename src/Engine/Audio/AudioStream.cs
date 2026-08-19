namespace Crowbar.Engine.Audio;

/// <summary>
/// Musique longue décodée par morceaux : un <see cref="IAudioDecoder"/> alimente
/// un ring buffer stéréo, et la voix consomme les frames au fil de l'eau. Le
/// fichier est chargé une fois et le décodage s'effectue en place, sans
/// allocation sur le thread DSP en régime permanent.
///
/// La lecture est toujours en stéréo (le décodeur normalise déjà les canaux).
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

    /// <summary>Durée connue du flux, ou <see cref="TimeSpan.Zero"/> si inconnue.</summary>
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
        // ~0,68 s de tampon, en stéréo.
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
