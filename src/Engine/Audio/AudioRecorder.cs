using Crowbar.FileSystems;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Enregistreur : deux modes complémentaires.
/// <list type="bullet">
/// <item><b>Sortie</b> (« record ce que tu entends ») : le thread DSP tape le
/// bus master après le mix et l'écrit en WAV — gratuit car le mixer est maison,
/// et c'est un différenciateur clé ;</item>
/// <item><b>Entrée</b> (micro) : un thread de capture défile le périphérique et
/// écrit le WAV.</item>
/// </list>
/// La boucle système (enregistrer les autres applications) est hors périmètre.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private readonly AudioSystem _system;
    private readonly float[] _scratch = new float[AudioSystem.BlockSize * 2];
    private WavWriter? _outputWriter;
    private WavWriter? _inputWriter;
    private IAudioCaptureDevice? _capture;
    private Thread? _captureThread;
    private volatile bool _capturing;
    private volatile bool _disposed;

    /// <summary>Vrai pendant un enregistrement de sortie (tap du master).</summary>
    public bool IsRecordingOutput => _outputWriter is not null;

    /// <summary>Vrai pendant une capture micro.</summary>
    public bool IsRecordingInput => _capturing;

    /// <summary>Chemin du fichier en cours d'écriture, ou null.</summary>
    public string? RecordingPath { get; private set; }

    internal AudioRecorder(AudioSystem system)
    {
        _system = system;
    }

    /// <summary>Commence à enregistrer la sortie master vers <paramref name="path"/> (projet).</summary>
    public void StartOutput(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_outputWriter is not null)
            return;

        EnsureDirectory(path);
        _outputWriter = new WavWriter(FileSystem.Project.OpenWrite(path), AudioSystem.SampleRate, AudioSystem.Channels);
        RecordingPath = path;
    }

    /// <summary>Commence à capturer le micro vers <paramref name="path"/> (projet).</summary>
    public void StartInput(string path, string? device = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_capturing || _system.Backend is null)
            return;

        var capture = _system.Backend.OpenCapture(device);
        if (capture is null)
            return;

        EnsureDirectory(path);
        _capture = capture;
        _inputWriter = new WavWriter(FileSystem.Project.OpenWrite(path), capture.SampleRate, capture.Channels);
        RecordingPath = path;
        _capturing = true;
        capture.Start();

        _captureThread = new Thread(CaptureLoop)
        {
            Name = "Crowbar.Audio.Capture",
            IsBackground = true
        };
        _captureThread.Start();
    }

    /// <summary>Arrête tous les enregistrements en cours et finalise les fichiers.</summary>
    public void Stop()
    {
        if (_capturing)
        {
            _capturing = false;
            _captureThread?.Join(2000);
            _captureThread = null;
            _capture?.Dispose();
            _capture = null;
            _inputWriter?.Close();
            _inputWriter = null;
        }

        if (_outputWriter is not null)
        {
            _outputWriter.Close();
            _outputWriter = null;
        }

        RecordingPath = null;
    }

    /// <summary>Tape le buffer master (appelé par le thread DSP après le mix).</summary>
    internal void TapOutput(ReadOnlySpan<float> interleaved, int frames)
    {
        var writer = _outputWriter;
        if (writer is null)
            return;

        interleaved[..(frames * 2)].CopyTo(_scratch);
        writer.WriteInterleaved(_scratch.AsSpan(0, frames * 2));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private void CaptureLoop()
    {
        var capture = _capture;
        if (capture is null)
            return;

        while (_capturing)
        {
            var read = capture.Read(_scratch);
            if (read > 0)
                _inputWriter?.WriteInterleaved(_scratch.AsSpan(0, read * 2));
            else
                Thread.Sleep(1);
        }
    }

    private static void EnsureDirectory(string path)
    {
        var directory = new FilePath(path).GetDirectory().FullName;
        if (directory.Length > 0 && !FileSystem.Project.DirectoryExists(directory))
            FileSystem.Project.CreateDirectory(directory);
    }
}
