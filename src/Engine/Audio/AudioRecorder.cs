using Crowbar.FileSystems;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Recorder: two complementary modes.
/// <list type="bullet">
/// <item><b>Output</b> ("record what you hear"): the DSP thread taps the master
/// bus after the mix and hands the block to a writer thread through an SPSC
/// ring, so the disk I/O never runs on the DSP thread;</item>
/// <item><b>Input</b> (microphone): a capture thread drains the device and
/// writes the WAV.</item>
/// </list>
/// The system loop (recording other applications) is out of scope.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private const int OutputRingBlocks = 256; // ~2.5 s of headroom @ 48 kHz.

    private readonly AudioSystem _system;
    private readonly float[] _scratch = new float[AudioSystem.BlockSize * 2];
    private readonly float[][] _outputRing;

    // SPSC output ring: the DSP thread produces blocks (Volatile.Write on
    // _outputProduced), the writer thread consumes them (Volatile.Read on
    // _outputConsumed). Blocks are pre-allocated so the DSP thread never
    // allocates while tapping.
    private long _outputProduced;
    private long _outputConsumed;

    private WavWriter? _outputWriter;
    private WavWriter? _inputWriter;
    private IAudioCaptureDevice? _capture;
    private Thread? _outputThread;
    private Thread? _captureThread;
    private bool _outputRunning;
    private volatile bool _capturing;
    private volatile bool _disposed;

    /// <summary>True during an output recording (master tap).</summary>
    public bool IsRecordingOutput => Volatile.Read(ref _outputRunning);

    /// <summary>True during a microphone capture.</summary>
    public bool IsRecordingInput => _capturing;

    /// <summary>Path of the file being written, or null.</summary>
    public string? RecordingPath { get; private set; }

    internal AudioRecorder(AudioSystem system)
    {
        _system = system;
        _outputRing = new float[OutputRingBlocks][];
        for (var i = 0; i < OutputRingBlocks; i++)
            _outputRing[i] = new float[AudioSystem.BlockSize * AudioSystem.Channels];
    }

    /// <summary>Starts recording the master output to <paramref name="path"/> (project).</summary>
    public void StartOutput(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Volatile.Read(ref _outputRunning))
            return;

        EnsureDirectory(path);
        _outputWriter = new WavWriter(FileSystem.Project.OpenWrite(path), AudioSystem.SampleRate, AudioSystem.Channels);
        RecordingPath = path;

        _outputProduced = 0;
        _outputConsumed = 0;
        Volatile.Write(ref _outputRunning, true);

        _outputThread = new Thread(OutputLoop)
        {
            Name = "Crowbar.Audio.OutputWriter",
            IsBackground = true
        };
        _outputThread.Start();
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

        if (Volatile.Read(ref _outputRunning))
        {
            Volatile.Write(ref _outputRunning, false);
            _outputThread?.Join(2000);
            _outputThread = null;
            _outputWriter?.Close();
            _outputWriter = null;
        }

        RecordingPath = null;
    }

    /// <summary>Taps the master buffer (called by the DSP thread after the mix).</summary>
    internal void TapOutput(ReadOnlySpan<float> interleaved, int frames)
    {
        if (!Volatile.Read(ref _outputRunning))
            return;

        var produced = _outputProduced;
        var consumed = Volatile.Read(ref _outputConsumed);

        // The writer cannot keep up: drop the block rather than block the DSP
        // thread (real-time safety). With ~2.5 s of headroom this is rare.
        if (produced - consumed >= OutputRingBlocks)
            return;

        var block = _outputRing[(int)(produced % OutputRingBlocks)];
        var count = Math.Min(interleaved.Length, block.Length);
        interleaved[..count].CopyTo(block);
        Volatile.Write(ref _outputProduced, produced + 1);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private void OutputLoop()
    {
        var writer = _outputWriter;
        if (writer is null)
            return;

        while (true)
        {
            var produced = Volatile.Read(ref _outputProduced);
            var consumed = _outputConsumed;
            if (consumed < produced)
            {
                writer.WriteInterleaved(_outputRing[(int)(consumed % OutputRingBlocks)]);
                Volatile.Write(ref _outputConsumed, consumed + 1);
                continue;
            }

            if (!Volatile.Read(ref _outputRunning))
                break;

            Thread.Sleep(1);
        }
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
