using System.Numerics;

namespace Crowbar.Engine.Audio;

/// <summary>The named buses of the mixing tree.</summary>
public enum AudioBusName
{
    Master,
    Music,
    Sfx,
    Ui,
    Voice
}

/// <summary>
/// The audio engine: bus tree (Master -> Music/SFX/UI/Voice), voice pool,
/// effects, SPSC command queue and DSP thread. The mixer is 100% C# and
/// deterministic: <see cref="RenderBlock"/> renders a block into a provided
/// buffer, without any backend — that is what the tests (and offline rendering)
/// call directly. When an <see cref="IAudioBackend"/> is provided,
/// <see cref="Start"/> launches the DSP thread that renders the blocks and
/// pushes them.
///
/// The game thread never touches the buffers: it reserves voices and sends
/// commands (Play, Stop, SetVolume, SetEffectParam, ...).
/// </summary>
public sealed class AudioSystem : IDisposable
{
    /// <summary>Engine sample rate (Hz).</summary>
    public const int SampleRate = 48000;

    /// <summary>Size of one DSP block, in frames (480 samples @ 48 kHz = 10 ms).</summary>
    public const int BlockSize = 480;

    /// <summary>Mixer channels (interleaved stereo).</summary>
    public const int Channels = 2;

    private readonly IAudioBackend? _backend;
    private readonly AudioBus[] _leafBuses;
    private readonly VoicePool _voices = new();
    private readonly AudioCommandQueue _commands = new();
    private readonly float[] _dspBuffer = new float[BlockSize * Channels];
    private Thread? _dspThread;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>Sample clock, the base of synchronization and offline rendering.</summary>
    public AudioClock Clock { get; } = new(SampleRate);

    /// <summary>3D listener for spatialization.</summary>
    public AudioListener Listener { get; } = new();

    public AudioBus Master { get; }
    public AudioBus Music { get; }
    public AudioBus Sfx { get; }
    public AudioBus Ui { get; }
    public AudioBus Voice { get; }

    /// <summary>Recorder (master tap + microphone capture).</summary>
    public AudioRecorder Recorder { get; }

    /// <summary>In-memory microphone input buffer (live read/replay).</summary>
    public Microphone Microphone { get; }

    public IAudioBackend? Backend => _backend;
    public bool IsRunning => _running;

    /// <summary>Number of currently active voices (diagnostic).</summary>
    public int ActiveVoiceCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < VoicePool.Capacity; i++)
            {
                if (_voices[i].State == (int)VoiceState.Active)
                    count++;
            }
            return count;
        }
    }

    public AudioSystem(IAudioBackend? backend = null)
    {
        _backend = backend;
        Master = new AudioBus("Master", BlockSize);
        Music = new AudioBus("Music", BlockSize);
        Sfx = new AudioBus("Sfx", BlockSize);
        Ui = new AudioBus("Ui", BlockSize);
        Voice = new AudioBus("Voice", BlockSize);
        _leafBuses = [Music, Sfx, Ui, Voice];
        Recorder = new AudioRecorder(this);
        Microphone = new Microphone(backend);
    }

    /// <summary>Returns a bus by its name.</summary>
    public AudioBus GetBus(AudioBusName name) => name switch
    {
        AudioBusName.Master => Master,
        AudioBusName.Music => Music,
        AudioBusName.Sfx => Sfx,
        AudioBusName.Ui => Ui,
        AudioBusName.Voice => Voice,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    /// <summary>Sets the linear volume of a bus.</summary>
    public void SetBusVolume(AudioBusName name, float volume) => GetBus(name).Gain = Math.Clamp(volume, 0f, 16f);

    /// <summary>Starts the DSP thread (only when a backend is attached).</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dspThread is not null)
            return;

        _running = true;
        if (_backend is null)
            return;

        _backend.Start();
        _dspThread = new Thread(DspLoop)
        {
            Name = "Crowbar.Audio.DSP",
            IsBackground = true
        };
        _dspThread.Start();
    }

    /// <summary>
    /// Game-thread tick (update phase). In offline mode (no backend), drains the
    /// command queue so <c>Play</c>/<c>Stop</c> take effect before the next
    /// render; with a DSP thread, the queue is already consumed by it.
    /// </summary>
    public void Update(float deltaTime)
    {
        if (_backend is null)
            DrainCommands();
    }

    /// <summary>Plays a clip (SFX by default) and returns its handle.</summary>
    public VoiceHandle Play(
        AudioClip clip,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return PlaySource(new ClipSource(clip), volume, pitch, pan, loop, fadeIn, priority, bus, spatial: false, position: default);
    }

    /// <summary>
    /// Loads a clip from a content path then plays it (short SFX). Equivalent to
    /// <c>Play(AudioClip.Load(path), ...)</c>; for long music, prefer
    /// <see cref="Play(AudioStream, float, float, float, bool, float, int, AudioBusName)"/>.
    /// </summary>
    public VoiceHandle Play(
        string path,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Play(AudioClip.Load(path), volume, pitch, pan, loop, fadeIn, priority, bus);
    }

    /// <summary>Plays a long stream (music) and returns its handle.</summary>
    public VoiceHandle Play(
        AudioStream stream,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Music)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return PlaySource(stream, volume, pitch, pan, loop, fadeIn, priority, bus, spatial: false, position: default);
    }

    /// <summary>Plays a spatialized clip (inverse-distance attenuation + equal-power pan).</summary>
    public VoiceHandle Play3D(
        AudioClip clip,
        Vector3 position,
        float volume = 1f,
        float pitch = 1f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return PlaySource(new ClipSource(clip), volume, pitch, 0f, loop, fadeIn, priority, bus, spatial: true, position);
    }

    /// <summary>Stops every voice (takes effect on the next block).</summary>
    public void StopAll()
    {
        var command = new AudioCommand { Type = AudioCommandType.StopAll };
        _commands.Enqueue(command);
    }

    /// <summary>True as long as the handle designates the current generation of its slot.</summary>
    internal bool IsVoiceAlive(int slot, int generation) =>
        slot >= 0 && slot < VoicePool.Capacity && _voices[slot].State != (int)VoiceState.Free && _voices[slot].Generation == generation;

    internal void EnqueueStop(int slot, int generation) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.Stop, Slot = slot, Generation = generation });

    internal void EnqueueSetVolume(int slot, int generation, float value) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.SetVolume, Slot = slot, Generation = generation, A = value });

    internal void EnqueueSetPitch(int slot, int generation, float value) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.SetPitch, Slot = slot, Generation = generation, A = value });

    internal void EnqueueSetPan(int slot, int generation, float value) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.SetPan, Slot = slot, Generation = generation, A = value });

    internal void EnqueueFadeTo(int slot, int generation, float volume, float duration) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.FadeTo, Slot = slot, Generation = generation, A = volume, B = duration });

    internal void EnqueueSetPosition(int slot, int generation, Vector3 position) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.SetPosition, Slot = slot, Generation = generation, Position = position });

    internal void EnqueueSetEffectParameter(int slot, int generation, int effectIndex, int parameterIndex, float value) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.SetEffectParam, Slot = slot, Generation = generation, Param0 = effectIndex, Param1 = parameterIndex, A = value });

    internal void EnqueueAddEffect(int slot, int generation, IAudioEffect effect) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.AddEffect, Slot = slot, Generation = generation, Effect = effect });

    /// <summary>
    /// Renders an interleaved stereo block into <paramref name="output"/>.
    /// Deterministic and allocation-free: first drains the command queue, sums
    /// the voices into the buses, applies the effect chains and copies the master.
    /// </summary>
    public void RenderBlock(Span<float> output)
    {
        if (output.Length < BlockSize * Channels)
            throw new ArgumentException($"The output buffer must hold at least {BlockSize * Channels} samples.", nameof(output));

        DrainCommands();
        var frames = BlockSize;
        var time = Clock.TimeSeconds;

        foreach (var bus in _leafBuses)
            bus.Clear();
        Master.Clear();

        // 1. The voices sum into their target bus.
        for (var i = 0; i < VoicePool.Capacity; i++)
        {
            var voice = _voices[i];
            if (voice.State == (int)VoiceState.Active)
            {
                voice.Render(Listener, frames, time);
                // The voice reached end of stream: free the slot. The
                // State == Active check guards against a concurrent steal (the
                // game thread may have re-reserved the slot in between).
                if (voice.HasEnded && voice.State == (int)VoiceState.Active)
                    voice.State = (int)VoiceState.Free;
            }
        }

        // 2. The leaf buses apply their effects then sum into the master.
        var anySolo = Music.Solo || Sfx.Solo || Ui.Solo || Voice.Solo;
        var masterAccumulator = Master.Accumulator;
        foreach (var bus in _leafBuses)
        {
            if (bus.Mute || (anySolo && !bus.Solo))
                continue;

            bus.ProcessEffects(frames, time);
            ComputeMeters(bus, frames);

            var gain = bus.Gain;
            var accumulator = bus.Accumulator;
            for (var i = 0; i < frames * Channels; i++)
                masterAccumulator[i] += accumulator[i] * gain;
        }

        // 3. Master effects and gain, then copy to the output.
        Master.ProcessEffects(frames, time);
        ComputeMeters(Master, frames);

        var masterGain = Master.Gain;
        for (var i = 0; i < frames * Channels; i++)
            output[i] = masterAccumulator[i] * masterGain;

        // 4. Output recording (tap after the mix), then advance the clock.
        Recorder.TapOutput(output, frames);
        Clock.Advance(frames);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _running = false;
        _dspThread?.Join(2000);
        _dspThread = null;
        _commands.Clear();
        Recorder.Dispose();
        Microphone.Dispose();
        _backend?.Dispose();
    }

    private VoiceHandle PlaySource(
        IAudioSource source,
        float volume,
        float pitch,
        float pan,
        bool loop,
        float fadeIn,
        int priority,
        AudioBusName bus,
        bool spatial,
        Vector3 position)
    {
        var (slot, generation) = _voices.Reserve(priority);
        var command = new AudioCommand
        {
            Type = AudioCommandType.Play,
            Slot = slot,
            Generation = generation,
            Source = source,
            Bus = GetBus(bus),
            A = volume,
            B = pitch,
            C = pan,
            D = loop ? 1f : 0f,
            E = fadeIn,
            Param0 = priority,
            Param1 = spatial ? 1 : 0,
            Position = position
        };
        _commands.Enqueue(command);
        return new VoiceHandle(this, slot, generation);
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
            ApplyCommand(command);
    }

    private void ApplyCommand(in AudioCommand command)
    {
        switch (command.Type)
        {
            case AudioCommandType.Play:
            {
                var voice = _voices[command.Slot];
                if (voice.Generation != command.Generation)
                    return;

                voice.Source = command.Source;
                voice.Target = command.Bus ?? Sfx;
                voice.Volume = command.A;
                voice.Pitch = command.B;
                voice.Pan = command.C;
                voice.Loop = command.D != 0f;
                voice.FadeInSeconds = command.E;
                voice.Priority = command.Param0;
                voice.Spatial = command.Param1 != 0;
                voice.Position = command.Position;
                voice.ClearEffects();
                voice.ResetRender();
                voice.State = (int)VoiceState.Active;
                break;
            }
            case AudioCommandType.Stop:
            {
                var voice = _voices[command.Slot];
                if (voice.Generation == command.Generation)
                {
                    voice.State = (int)VoiceState.Free;
                    voice.Source = null;
                }
                break;
            }
            case AudioCommandType.SetVolume:
            {
                var voice = _voices[command.Slot];
                if (voice.Generation == command.Generation)
                    voice.Volume = command.A;
                break;
            }
            case AudioCommandType.SetPitch:
            {
                var voice = _voices[command.Slot];
                if (voice.Generation == command.Generation)
                    voice.Pitch = command.B;
                break;
            }
            case AudioCommandType.SetPan:
            {
                var voice = _voices[command.Slot];
                if (voice.Generation == command.Generation)
                    voice.Pan = command.A;
                break;
            }
            case AudioCommandType.FadeTo:
            {
                var voice = _voices[command.Slot];
                if (voice.Generation == command.Generation)
                    voice.FadeTo(command.A, command.B);
                break;
            }
            case AudioCommandType.SetPosition:
            {
                var voice = _voices[command.Slot];
                if (voice.Generation == command.Generation)
                    voice.Position = command.Position;
                break;
            }
            case AudioCommandType.AddEffect:
            {
                var voice = _voices[command.Slot];
                if (voice.Generation == command.Generation && command.Effect is not null)
                    voice.AddEffect(command.Effect);
                break;
            }
            case AudioCommandType.SetEffectParam:
            {
                var voice = _voices[command.Slot];
                if (voice.Generation == command.Generation)
                    voice.SetEffectParameter(command.Param0, command.Param1, command.A);
                break;
            }
            case AudioCommandType.StopAll:
                for (var i = 0; i < VoicePool.Capacity; i++)
                {
                    var voice = _voices[i];
                    voice.State = (int)VoiceState.Free;
                    voice.Source = null;
                }
                break;
        }
    }

    private static void ComputeMeters(AudioBus bus, int frames)
    {
        var accumulator = bus.Accumulator;
        var peak = 0f;
        var sumSquares = 0f;
        for (var i = 0; i < frames * Channels; i++)
        {
            var sample = accumulator[i];
            var magnitude = MathF.Abs(sample);
            if (magnitude > peak)
                peak = magnitude;
            sumSquares += sample * sample;
        }

        bus.SetMeters(peak, MathF.Sqrt(sumSquares / (frames * Channels)), 0.9f);
    }

    private void DspLoop()
    {
        // ~8 queued blocks = ~80 ms of headroom: enough to absorb scheduling
        // jitter without adding perceptible latency.
        const uint maxQueued = BlockSize * 8;

        while (_running)
        {
            var backend = _backend;
            if (backend is null || !backend.IsRunning)
            {
                Thread.Sleep(1);
                continue;
            }

            if (backend.QueuedSamples > maxQueued)
            {
                Thread.Sleep(1);
                continue;
            }

            RenderBlock(_dspBuffer);
            backend.QueueSamples(_dspBuffer);
        }
    }
}
