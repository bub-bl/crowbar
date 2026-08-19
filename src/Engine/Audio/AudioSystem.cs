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
/// The audio engine: bus tree (Master -> Music/SFX/UI/Voice), sound pool,
/// effects, SPSC command queue and DSP thread. The mixer is 100% C# and
/// deterministic: <see cref="RenderBlock"/> renders a block into a provided
/// buffer, without any backend — that is what the tests (and offline rendering)
/// call directly. When an <see cref="IAudioBackend"/> is provided,
/// <see cref="Start"/> launches the DSP thread that renders the blocks and
/// pushes them.
///
/// The game thread never touches the buffers: it reserves sounds and sends
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
    private AudioBus[] _leafBuses;
    private readonly SoundPool _sounds = new();
    private readonly AudioCommandQueue _commands = new();
    private readonly AudioCommandQueue _events = new();
    private readonly float[] _dspBuffer = new float[BlockSize * Channels];
    private readonly List<AudioCommand> _pendingPlays = new();
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

    /// <summary>Number of currently active sounds (diagnostic).</summary>
    public int ActiveSoundCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < SoundPool.Capacity; i++)
            {
                if (_sounds[i].State == (int)SoundState.Active)
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

    /// <summary>Returns a built-in bus by its name.</summary>
    public AudioBus GetBus(AudioBusName name) => name switch
    {
        AudioBusName.Master => Master,
        AudioBusName.Music => Music,
        AudioBusName.Sfx => Sfx,
        AudioBusName.Ui => Ui,
        AudioBusName.Voice => Voice,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    /// <summary>Returns a bus by its name (built-in or user-created), or null when unknown.</summary>
    public AudioBus? GetBus(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (string.Equals(name, Master.Name, StringComparison.OrdinalIgnoreCase))
            return Master;
        foreach (var bus in Volatile.Read(ref _leafBuses))
        {
            if (string.Equals(bus.Name, name, StringComparison.OrdinalIgnoreCase))
                return bus;
        }
        return null;
    }

    /// <summary>
    /// Creates a new leaf bus that sums into the master, usable immediately (also
    /// while the DSP thread is running). The name must not collide with an
    /// existing bus or with the built-in <c>Master</c>.
    /// </summary>
    public AudioBus CreateBus(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (string.Equals(name, Master.Name, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The bus name '{name}' is reserved.", nameof(name));

        var buses = Volatile.Read(ref _leafBuses);
        foreach (var bus in buses)
        {
            if (string.Equals(bus.Name, name, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"A bus named '{name}' already exists.", nameof(name));
        }

        var created = new AudioBus(name, BlockSize);
        var copy = new AudioBus[buses.Length + 1];
        Array.Copy(buses, copy, buses.Length);
        copy[^1] = created;
        Volatile.Write(ref _leafBuses, copy);
        return created;
    }

    /// <summary>
    /// Removes a user-created bus: stops every sound routed to it and drops it
    /// from the mixing tree. Returns false when no such bus exists. Built-in
    /// buses cannot be removed.
    /// </summary>
    public bool RemoveBus(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var buses = Volatile.Read(ref _leafBuses);
        for (var i = 0; i < buses.Length; i++)
        {
            if (!string.Equals(buses[i].Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            var bus = buses[i];
            if (ReferenceEquals(bus, Music) || ReferenceEquals(bus, Sfx) || ReferenceEquals(bus, Ui) || ReferenceEquals(bus, Voice))
                throw new InvalidOperationException($"The built-in bus '{name}' cannot be removed.");
            _commands.Enqueue(new AudioCommand { Type = AudioCommandType.StopBus, Bus = bus });

            var copy = new AudioBus[buses.Length - 1];
            Array.Copy(buses, 0, copy, 0, i);
            Array.Copy(buses, i + 1, copy, i, buses.Length - i - 1);
            Volatile.Write(ref _leafBuses, copy);
            return true;
        }
        return false;
    }

    /// <summary>Names of the leaf buses (built-in + user-created), master excluded.</summary>
    public string[] BusNames => Volatile.Read(ref _leafBuses).Select(bus => bus.Name).ToArray();

    /// <summary>Sets the linear volume of a bus.</summary>
    public void SetBusVolume(AudioBusName name, float volume) => GetBus(name).Gain = Math.Clamp(volume, 0f, 16f);

    /// <summary>Sets the linear volume of a bus by name.</summary>
    public void SetBusVolume(string name, float volume)
    {
        var bus = GetBus(name) ?? throw new ArgumentException($"Bus '{name}' not found.", nameof(name));
        bus.Gain = Math.Clamp(volume, 0f, 16f);
    }

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
    /// render; with a DSP thread, the queue is already consumed by it. Always
    /// dispatches sound-completion callbacks produced by the DSP thread.
    /// </summary>
    public void Update(float deltaTime)
    {
        if (_backend is null)
            DrainCommands();

        DrainEvents();
    }

    /// <summary>Plays a clip (SFX by default) and returns its handle.</summary>
    public SoundHandle Play(
        AudioClip clip,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return PlaySource(new ClipSource(clip), volume, pitch, pan, loop, fadeIn, priority, GetBus(bus), spatial: false, position: default, onCompleted);
    }

    /// <summary>Plays a clip on a user-created bus (see <see cref="CreateBus"/>).</summary>
    public SoundHandle Play(
        AudioClip clip,
        AudioBus bus,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(bus);
        return PlaySource(new ClipSource(clip), volume, pitch, pan, loop, fadeIn, priority, bus, spatial: false, position: default, onCompleted);
    }

    /// <summary>Plays a clip on a bus looked up by name (throws when the bus does not exist).</summary>
    public SoundHandle Play(
        AudioClip clip,
        string bus,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return PlaySource(new ClipSource(clip), volume, pitch, pan, loop, fadeIn, priority, GetBus(bus) ?? throw new ArgumentException($"Bus '{bus}' not found.", nameof(bus)), spatial: false, position: default, onCompleted);
    }

    /// <summary>
    /// Loads a clip from a content path then plays it (short SFX). Equivalent to
    /// <c>Play(AudioClip.Load(path), ...)</c>; for long music, prefer
    /// <see cref="Play(AudioStream, float, float, float, bool, float, int, AudioBusName)"/>.
    /// </summary>
    public SoundHandle Play(
        string path,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx,
        Action? onCompleted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Play(AudioClip.Load(path), volume, pitch, pan, loop, fadeIn, priority, bus, onCompleted);
    }

    /// <summary>
    /// Loads a clip from a content path off the calling thread, then plays it
    /// (short SFX). Equivalent to <c>Play(await AudioClip.LoadAsync(path), ...)</c>.
    /// </summary>
    public async Task<SoundHandle> PlayAsync(
        string path,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx,
        Action? onCompleted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var clip = await AudioClip.LoadAsync(path).ConfigureAwait(false);
        return Play(clip, volume, pitch, pan, loop, fadeIn, priority, bus, onCompleted);
    }

    /// <summary>Plays a long stream (music) and returns its handle.</summary>
    public SoundHandle Play(
        AudioStream stream,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Music,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return PlaySource(stream, volume, pitch, pan, loop, fadeIn, priority, GetBus(bus), spatial: false, position: default, onCompleted);
    }

    /// <summary>Plays a long stream on a user-created bus (see <see cref="CreateBus"/>).</summary>
    public SoundHandle Play(
        AudioStream stream,
        AudioBus bus,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(bus);
        return PlaySource(stream, volume, pitch, pan, loop, fadeIn, priority, bus, spatial: false, position: default, onCompleted);
    }

    /// <summary>Plays a long stream on a bus looked up by name.</summary>
    public SoundHandle Play(
        AudioStream stream,
        string bus,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return PlaySource(stream, volume, pitch, pan, loop, fadeIn, priority, GetBus(bus) ?? throw new ArgumentException($"Bus '{bus}' not found.", nameof(bus)), spatial: false, position: default, onCompleted);
    }

    /// <summary>Plays a spatialized clip (inverse-distance attenuation + equal-power pan).</summary>
    public SoundHandle Play3D(
        AudioClip clip,
        Vector3 position,
        float volume = 1f,
        float pitch = 1f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return PlaySource(new ClipSource(clip), volume, pitch, 0f, loop, fadeIn, priority, GetBus(bus), spatial: true, position, onCompleted);
    }

    /// <summary>Plays a spatialized clip on a user-created bus.</summary>
    public SoundHandle Play3D(
        AudioClip clip,
        Vector3 position,
        AudioBus bus,
        float volume = 1f,
        float pitch = 1f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(bus);
        return PlaySource(new ClipSource(clip), volume, pitch, 0f, loop, fadeIn, priority, bus, spatial: true, position, onCompleted);
    }

    /// <summary>Plays a spatialized clip on a bus looked up by name.</summary>
    public SoundHandle Play3D(
        AudioClip clip,
        Vector3 position,
        string bus,
        float volume = 1f,
        float pitch = 1f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return PlaySource(new ClipSource(clip), volume, pitch, 0f, loop, fadeIn, priority, GetBus(bus) ?? throw new ArgumentException($"Bus '{bus}' not found.", nameof(bus)), spatial: true, position, onCompleted);
    }

    /// <summary>Plays a spatialized stream at <paramref name="position"/> (ambience).</summary>
    public SoundHandle Play3D(
        AudioStream stream,
        Vector3 position,
        float volume = 1f,
        float pitch = 1f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return PlaySource(stream, volume, pitch, 0f, loop, fadeIn, priority, GetBus(bus), spatial: true, position, onCompleted);
    }

    /// <summary>
    /// Plays a clip after <paramref name="delay"/> seconds (measured from the
    /// audio clock). The handle is valid immediately and the sound starts when
    /// the clock reaches the target instant; callbacks fire once it ends.
    /// </summary>
    public SoundHandle PlayAt(
        AudioClip clip,
        float delay,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return PlaySource(new ClipSource(clip), volume, pitch, pan, loop, fadeIn, priority, GetBus(bus), spatial: false, position: default, onCompleted, Clock.TimeSeconds + Math.Max(0f, delay));
    }

    /// <summary>Plays a stream after <paramref name="delay"/> seconds.</summary>
    public SoundHandle PlayAt(
        AudioStream stream,
        float delay,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Music,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return PlaySource(stream, volume, pitch, pan, loop, fadeIn, priority, GetBus(bus), spatial: false, position: default, onCompleted, Clock.TimeSeconds + Math.Max(0f, delay));
    }

    /// <summary>
    /// Crossfades the music on a bus: every sound currently routed to it fades
    /// out over <paramref name="duration"/> seconds while <paramref name="stream"/>
    /// fades in over the same window. Any still-parked <see cref="PlayAt"/>
    /// scheduled on that bus is cancelled.
    /// </summary>
    public SoundHandle Crossfade(
        AudioStream stream,
        float duration,
        AudioBusName bus = AudioBusName.Music,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Crossfade(stream, duration, GetBus(bus), onCompleted);
    }

    /// <summary>Crossfades the sounds on a bus looked up by name.</summary>
    public SoundHandle Crossfade(
        AudioStream stream,
        float duration,
        string bus,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Crossfade(stream, duration, GetBus(bus) ?? throw new ArgumentException($"Bus '{bus}' not found.", nameof(bus)), onCompleted);
    }

    /// <summary>Crossfades the sounds on a specific bus.</summary>
    public SoundHandle Crossfade(
        AudioStream stream,
        float duration,
        AudioBus bus,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(bus);

        var (slot, generation) = _sounds.Reserve(0);
        _commands.Enqueue(new AudioCommand
        {
            Type = AudioCommandType.Crossfade,
            Slot = slot,
            Generation = generation,
            Bus = bus,
            B = duration
        });
        _commands.Enqueue(new AudioCommand
        {
            Type = AudioCommandType.Play,
            Slot = slot,
            Generation = generation,
            Source = stream,
            Bus = bus,
            Callback = onCompleted,
            A = 1f,
            B = 1f,
            C = 0f,
            D = 1f,
E = duration
        });
        return new SoundHandle(this, slot, generation);
    }

    /// <summary>
    /// Plays a random variant from <paramref name="cue"/> with the cue's
    /// randomized volume/pitch/pan (see <see cref="SoundCue"/>).
    /// </summary>
    public SoundHandle Play(
        SoundCue cue,
        AudioBusName bus = AudioBusName.Sfx,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(cue);
        var (clip, volume, pitch, pan) = cue.Roll();
        return PlaySource(new ClipSource(clip), volume, pitch, pan, cue.Loop, 0f, cue.Priority, GetBus(bus), spatial: false, position: default, onCompleted);
    }

    /// <summary>Plays a random cue variant on a user-created bus.</summary>
    public SoundHandle Play(
        SoundCue cue,
        AudioBus bus,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(cue);
        var (clip, volume, pitch, pan) = cue.Roll();
        return PlaySource(new ClipSource(clip), volume, pitch, pan, cue.Loop, 0f, cue.Priority, bus, spatial: false, position: default, onCompleted);
    }

    /// <summary>Plays a spatialized random cue variant at <paramref name="position"/>.</summary>
    public SoundHandle Play3D(
        SoundCue cue,
        Vector3 position,
        AudioBusName bus = AudioBusName.Sfx,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(cue);
        var (clip, volume, pitch, pan) = cue.Roll();
        return PlaySource(new ClipSource(clip), volume, pitch, pan, cue.Loop, 0f, cue.Priority, GetBus(bus), spatial: true, position, onCompleted);
    }

    /// <summary>Stops every sound.</summary>
    public void StopAll() =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.StopAll });

    /// <summary>Stops every sound routed to a built-in bus.</summary>
    public void StopAll(AudioBusName bus) => StopAll(GetBus(bus));

    /// <summary>Stops every sound routed to a bus looked up by name.</summary>
    public void StopAll(string bus)
    {
        var target = GetBus(bus) ?? throw new ArgumentException($"Bus '{bus}' not found.", nameof(bus));
        StopAll(target);
    }

    /// <summary>Stops every sound routed to a specific bus.</summary>
    public void StopAll(AudioBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.StopBus, Bus = bus });
    }

    /// <summary>True as long as the handle designates the current generation of its slot.</summary>
    internal bool IsSoundAlive(int slot, int generation) =>
        slot >= 0 && slot < SoundPool.Capacity && _sounds[slot].State != (int)SoundState.Free && _sounds[slot].Generation == generation;

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

    internal void EnqueueSetVelocity(int slot, int generation, Vector3 velocity) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.SetVelocity, Slot = slot, Generation = generation, Position = velocity });

    internal void EnqueuePause(int slot, int generation) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.Pause, Slot = slot, Generation = generation });

    internal void EnqueueResume(int slot, int generation) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.Resume, Slot = slot, Generation = generation });

    internal void EnqueueFadeStop(int slot, int generation, float duration) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.FadeStop, Slot = slot, Generation = generation, A = duration });

    internal bool IsSoundPaused(int slot, int generation) =>
        slot >= 0 && slot < SoundPool.Capacity && _sounds[slot].Generation == generation && _sounds[slot].Paused;

    internal float GetSoundPositionSeconds(int slot, int generation) =>
        slot >= 0 && slot < SoundPool.Capacity && _sounds[slot].Generation == generation ? _sounds[slot].PlaybackSeconds : 0f;

    internal float GetSoundDurationSeconds(int slot, int generation) =>
        slot >= 0 && slot < SoundPool.Capacity && _sounds[slot].Generation == generation ? _sounds[slot].DurationSeconds : 0f;

    internal void EnqueueSetEffectParameter(int slot, int generation, int effectIndex, int parameterIndex, float value) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.SetEffectParam, Slot = slot, Generation = generation, Param0 = effectIndex, Param1 = parameterIndex, A = value });

    internal void EnqueueSetEffectParameter(int slot, int generation, int effectIndex, string name, float value) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.SetEffectParamByName, Slot = slot, Generation = generation, Param0 = effectIndex, Name = name, A = value });

    internal void EnqueueAddEffect(int slot, int generation, IAudioEffect effect) =>
        _commands.Enqueue(new AudioCommand { Type = AudioCommandType.AddEffect, Slot = slot, Generation = generation, Effect = effect });

    /// <summary>
    /// Renders an interleaved stereo block into <paramref name="output"/>.
    /// Deterministic and allocation-free: first drains the command queue, sums
    /// the sounds into the buses, applies the effect chains and copies the master.
    /// </summary>
    public void RenderBlock(Span<float> output)
    {
        if (output.Length < BlockSize * Channels)
            throw new ArgumentException($"The output buffer must hold at least {BlockSize * Channels} samples.", nameof(output));

        DrainCommands();
        ProcessPendingPlays();
        var frames = BlockSize;
        var time = Clock.TimeSeconds;

        var buses = Volatile.Read(ref _leafBuses);
        foreach (var bus in buses)
            bus.Clear();
        Master.Clear();

        // 1. The sounds sum into their target bus.
        for (var i = 0; i < SoundPool.Capacity; i++)
        {
            var sound = _sounds[i];
            if (sound.State == (int)SoundState.Active)
            {
                sound.Render(Listener, frames, time);
                // The sound reached end of stream: free the slot and publish
                // its completion callback (if any). The State == Active check
                // guards against a concurrent steal (the game thread may have
                // re-reserved the slot in between).
                if (sound.HasEnded && sound.State == (int)SoundState.Active)
                {
                    var completed = sound.Completed;
                    sound.Completed = null;
                    sound.Source = null;
                    sound.State = (int)SoundState.Free;
                    if (completed is not null)
                        _events.Enqueue(new AudioCommand { Type = AudioCommandType.SoundEnded, Callback = completed });
                }
            }
        }

        // 2. The leaf buses apply their effects then sum into the master.
        var anySolo = false;
        foreach (var bus in buses)
        {
            if (bus.Solo)
            {
                anySolo = true;
                break;
            }
        }

        var masterAccumulator = Master.Accumulator;
        foreach (var bus in buses)
        {
            if (bus.Mute || (anySolo && !bus.Solo))
                continue;

            bus.ProcessEffects(frames, time);
            ComputeMeters(bus, frames);

            // Smooth the bus gain over the block (~10 ms ramp) so volume
            // changes do not produce audible steps.
            var accumulator = bus.Accumulator;
            var count = frames * Channels;
            var g0 = bus.SmoothedGain;
            var g1 = bus.Gain;
            bus.SmoothedGain = g1;
            if (g0 == g1)
            {
                for (var i = 0; i < count; i++)
                    masterAccumulator[i] += accumulator[i] * g1;
            }
            else
            {
                var inv = 1f / count;
                for (var i = 0; i < count; i++)
                    masterAccumulator[i] += accumulator[i] * (g0 + (g1 - g0) * (i * inv));
            }
        }

        // 3. Master effects and gain, then copy to the output.
        Master.ProcessEffects(frames, time);
        ComputeMeters(Master, frames);

        var masterAccumulator2 = Master.Accumulator;
        var masterCount = frames * Channels;
        var mg0 = Master.SmoothedGain;
        var mg1 = Master.Gain;
        Master.SmoothedGain = mg1;
        if (mg0 == mg1)
        {
            for (var i = 0; i < masterCount; i++)
                output[i] = masterAccumulator2[i] * mg1;
        }
        else
        {
            var inv = 1f / masterCount;
            for (var i = 0; i < masterCount; i++)
                output[i] = masterAccumulator2[i] * (mg0 + (mg1 - mg0) * (i * inv));
        }

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
        _events.Clear();
        Recorder.Dispose();
        Microphone.Dispose();
        _backend?.Dispose();
    }

    private SoundHandle PlaySource(
        IAudioSource source,
        float volume,
        float pitch,
        float pan,
        bool loop,
        float fadeIn,
        int priority,
        AudioBus targetBus,
        bool spatial,
        Vector3 position,
        Action? onCompleted,
        double scheduledAt = 0d)
    {
        // Sources that loop internally (threaded streams) read this flag from
        // the decoder thread; clip sources ignore it (the sound rewinds them).
        source.Loop = loop;

        var (slot, generation) = _sounds.Reserve(priority);
        var command = new AudioCommand
        {
            Type = AudioCommandType.Play,
            Slot = slot,
            Generation = generation,
            Source = source,
            Bus = targetBus,
            Callback = onCompleted,
            A = volume,
            B = pitch,
            C = pan,
            D = loop ? 1f : 0f,
            E = fadeIn,
            Param0 = priority,
            Param1 = spatial ? 1 : 0,
            Position = position,
            ScheduledAt = scheduledAt
        };
        _commands.Enqueue(command);
        return new SoundHandle(this, slot, generation);
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
            ApplyCommand(command);
    }

    /// <summary>
    /// Dispatches sound-completion callbacks on the game thread. The DSP thread
    /// (or <see cref="RenderBlock"/> in offline mode) produces them through the
    /// reverse SPSC queue.
    /// </summary>
    private void DrainEvents()
    {
        while (_events.TryDequeue(out var command))
        {
            if (command.Type == AudioCommandType.SoundEnded)
                command.Callback?.Invoke();
        }
    }

    private void ApplyCommand(in AudioCommand command)
    {
        switch (command.Type)
        {
            case AudioCommandType.Play:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation != command.Generation)
                    return;

                // Scheduled play (PlayAt): keep it parked until the clock
                // reaches the target instant, then apply it like any other.
                if (command.ScheduledAt > Clock.TimeSeconds)
                {
                    _pendingPlays.Add(command);
                    return;
                }

                sound.Source = command.Source;
                sound.Target = command.Bus ?? Sfx;
                sound.Volume = command.A;
                sound.Pitch = command.B;
                sound.Pan = command.C;
                sound.Loop = command.D != 0f;
                sound.FadeInSeconds = command.E;
                sound.Priority = command.Param0;
                sound.Spatial = command.Param1 != 0;
                sound.Position = command.Position;
                sound.Velocity = default;
                sound.Paused = false;
                sound.Completed = command.Callback;
                sound.SourceSampleRate = command.Source?.SampleRate ?? SampleRate;
                sound.DurationSeconds = command.Source is null
                    ? 0f
                    : command.Source.TotalFrames > 0
                        ? command.Source.TotalFrames / (float)command.Source.SampleRate
                        : -1f;
                sound.ClearEffects();
                sound.ResetRender();
                sound.State = (int)SoundState.Active;
                break;
            }
            case AudioCommandType.Stop:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation == command.Generation)
                {
                    sound.State = (int)SoundState.Free;
                    sound.Source = null;
                    sound.Completed = null;
                }
                CancelPendingPlay(command.Slot);
                break;
            }
            case AudioCommandType.SetVolume:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation == command.Generation)
                    sound.Volume = command.A;
                break;
            }
            case AudioCommandType.SetPitch:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation == command.Generation)
                    sound.Pitch = command.B;
                break;
            }
            case AudioCommandType.SetPan:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation == command.Generation)
                    sound.Pan = command.A;
                break;
            }
            case AudioCommandType.FadeTo:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation == command.Generation)
                    sound.FadeTo(command.A, command.B);
                break;
            }
            case AudioCommandType.SetPosition:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation == command.Generation)
                    sound.Position = command.Position;
                break;
            }
            case AudioCommandType.SetVelocity:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation == command.Generation)
                    sound.Velocity = command.Position;
                break;
            }
            case AudioCommandType.Pause:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation == command.Generation)
                    sound.Paused = true;
                break;
            }
            case AudioCommandType.Resume:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation == command.Generation)
                    sound.Paused = false;
                break;
            }
            case AudioCommandType.FadeStop:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation == command.Generation)
                    sound.FadeToStop(command.A);
                break;
            }
            case AudioCommandType.AddEffect:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation == command.Generation && command.Effect is not null)
                    sound.AddEffect(command.Effect);
                break;
            }
            case AudioCommandType.SetEffectParam:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation == command.Generation)
                    sound.SetEffectParameter(command.Param0, command.Param1, command.A);
                break;
            }
            case AudioCommandType.SetEffectParamByName:
            {
                var sound = _sounds[command.Slot];
                if (sound.Generation == command.Generation && command.Name is not null)
                    sound.SetEffectParameter(command.Param0, command.Name, command.A);
                break;
            }
            case AudioCommandType.Crossfade:
            {
                var bus = command.Bus;
                if (bus is null)
                    break;

                // Cancel still-parked PlayAt commands on this bus, then fade
                // every sound currently routed there down to silence. The new
                // stream (with a matching fade-in) is enqueued right after.
                CancelPendingPlays(bus);
                for (var i = 0; i < SoundPool.Capacity; i++)
                {
                    var sound = _sounds[i];
                    if (ReferenceEquals(sound.Target, bus) && sound.State != (int)SoundState.Free)
                        sound.FadeToStop(command.B);
                }
                break;
            }
            case AudioCommandType.StopAll:
                _pendingPlays.Clear();
                for (var i = 0; i < SoundPool.Capacity; i++)
                {
                    var sound = _sounds[i];
                    sound.State = (int)SoundState.Free;
                    sound.Source = null;
                    sound.Completed = null;
                }
                break;
            case AudioCommandType.StopBus:
            {
                var bus = command.Bus;
                if (bus is not null)
                    CancelPendingPlays(bus);
                for (var i = 0; i < SoundPool.Capacity; i++)
                {
                    var sound = _sounds[i];
                    if (ReferenceEquals(sound.Target, bus) && sound.State != (int)SoundState.Free)
                    {
                        sound.State = (int)SoundState.Free;
                        sound.Source = null;
                        sound.Completed = null;
                    }
                }
                break;
            }
        }
    }

    private void ProcessPendingPlays()
    {
        if (_pendingPlays.Count == 0)
            return;

        for (var i = 0; i < _pendingPlays.Count;)
        {
            var command = _pendingPlays[i];
            if (command.ScheduledAt <= Clock.TimeSeconds)
            {
                _pendingPlays.RemoveAt(i);
                ApplyCommand(command);
            }
            else
            {
                i++;
            }
        }
    }

    private void CancelPendingPlay(int slot)
    {
        for (var i = _pendingPlays.Count - 1; i >= 0; i--)
        {
            if (_pendingPlays[i].Slot == slot)
                _pendingPlays.RemoveAt(i);
        }
    }

    private void CancelPendingPlays(AudioBus bus)
    {
        for (var i = _pendingPlays.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_pendingPlays[i].Bus, bus))
                _pendingPlays.RemoveAt(i);
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
