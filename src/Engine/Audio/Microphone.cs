namespace Crowbar.Engine.Audio;

/// <summary>
/// Tampon d'entrée micro en mémoire : un thread de capture défile le
/// périphérique et remplit un ring buffer stéréo flottant. Le jeu peut alors
/// soit <see cref="Read"/> (consommer en flux, pour du traitement temps réel),
/// soit <see cref="TakeClip"/> (instantané des derniers instants, à rejouer via
/// <see cref="Audio.Play(AudioClip, float, float, float, bool, float, int, AudioBusName)"/>).
///
/// Complémentaire de <see cref="AudioRecorder.StartInput"/> : ici rien n'est
/// écrit sur disque, les échantillons restent disponibles côté CPU.
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

    /// <summary>Le moteur normalise toujours en stéréo entrelacé.</summary>
    public int Channels => 2;

    /// <summary>Vrai pendant que la capture tourne.</summary>
    public bool IsActive => _running;

    /// <summary>Capacité du ring buffer, en frames.</summary>
    public int CapacityFrames
    {
        get
        {
            lock (_gate)
                return _capacityFrames;
        }
    }

    /// <summary>Frames capturées disponibles à la lecture (non consommées).</summary>
    public int AvailableFrames
    {
        get
        {
            lock (_gate)
                return _available;
        }
    }

    /// <summary>Nombre total de frames capturées depuis le démarrage.</summary>
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
        // Un tampon par défaut d'une seconde ; Start peut le redimensionner.
        _capacityFrames = AudioSystem.SampleRate;
        _buffer = new float[_capacityFrames * 2];
    }

    /// <summary>
    /// Ouvre le périphérique de capture et démarre le thread qui remplit le
    /// ring buffer. Retourne false si aucun backend ou périphérique n'est
    /// disponible.
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

    /// <summary>Arrête la capture et libère le périphérique (le buffer reste lisible).</summary>
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
    /// Consomme jusqu'à <c>destination.Length / 2</c> frames (les plus
    /// anciennes) et retourne le nombre de frames lues.
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
    /// Instantané non destructif des derniers instants capturés, prêt à être
    /// rejoué. <paramref name="seconds"/> vaut 0 pour tout le buffer ; sinon le
    /// clip contient au plus <paramref name="seconds"/> secondes (les plus
    /// récentes).
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

    /// <summary>Écrit des frames stéréo dans le ring buffer (thread capture, ou test).</summary>
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
        // Frame la plus ancienne encore disponible (le ring buffer se
        // réécrit par-dessus les frames consommées/anciennes).
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
