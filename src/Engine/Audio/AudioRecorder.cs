using Crowbar.FileSystems;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Recorder: two complementary modes.
/// <list type="bullet">
/// <item><b>Output</b> ("record what you hear"): the DSP thread taps the master
/// bus after the mix and writes it as WAV — free because the mixer is homegrown,
/// and a key differentiator;</item>
/// <item><b>Input</b> (microphone): a capture thread drains the device and
/// writes the WAV.</item>
/// </list>
/// The system loop (recording other applications) is out of scope.
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

    /// <summary>True during an output recording (master tap).</summary>
    public bool IsRecordingOutput => _outputWriter is not null;

    /// <summary>True during a microphone capture.</summary>
    public bool IsRecordingInput => _capturing;

    /// <summary>Path of the file being written, or null.</summary>
    public string? RecordingPath { get; private set; }

    internal AudioRecorder(AudioSystem system)
    {
        _system = system;
    }

    /// <summary>Starts recording the master output to <paramref name="path"/> (project).</summary>
    public void StartOutput(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_outputWriter is not null)
            return;

        EnsureDirectory(path);
        _outputWriter = new WavWriter(FileSystem.Project.OpenWrite(path), AudioSystem.SampleRate, AudioSystem.Channels);
        RecordingPath = path;
    }

    /// <summary>Starts capturing the microphone to <paramref name="path"/> (project).</summary>
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

    /// <summary>Stops every ongoing recording and finalizes the files.</summary>
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

    /// <summary>Taps the master buffer (called by the DSP thread after the mix).</summary>
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
