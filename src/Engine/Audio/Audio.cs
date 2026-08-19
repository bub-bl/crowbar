using System.Numerics;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Global audio facade, on the same model as <see cref="Crowbar.Engine.InputSystem.Input"/> and
/// <see cref="Crowbar.Engine.InputSystem.Mouse"/>: call <c>Audio.Bind(...)</c> once at
/// startup, then <c>Audio.Play(clip)</c> from anywhere, including a scripted
/// gamemode. Every method is safe without a bound backend (they become no-ops)
/// and returns a <see cref="VoiceHandle"/> to drive the voice (volume, pitch,
/// pan, fade, 3D position, effects).
/// </summary>
public static class Audio
{
    private static AudioSystem? _system;

    /// <summary>Binds the audio engine to the static facade.</summary>
    public static void Bind(AudioSystem system)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
    }

    /// <summary>Unbinds the facade (called when the engine is destroyed).</summary>
    public static void Unbind() => _system = null;

    /// <summary>True once <see cref="Bind"/> has been called.</summary>
    public static bool IsBound => _system is not null;

    /// <summary>The 3D listener (position/orientation for <see cref="Play3D"/>), or null when unbound.</summary>
    public static AudioListener? Listener => _system?.Listener;

    /// <summary>The in-memory microphone buffer (null until Audio is bound).</summary>
    public static Microphone? Microphone => _system?.Microphone;

    /// <summary>Plays a short clip (SFX) and returns its handle.</summary>
    public static VoiceHandle Play(
        AudioClip clip,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx,
        Action? onCompleted = null) =>
        _system?.Play(clip, volume, pitch, pan, loop, fadeIn, priority, bus, onCompleted) ?? VoiceHandle.Invalid;

    /// <summary>
    /// Loads a clip from a content path then plays it (short SFX). Throws if the
    /// file is not found; for long music use <see cref="Play(AudioStream, float, float, float, bool, float, int, AudioBusName)"/>.
    /// </summary>
    public static VoiceHandle Play(
        string path,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx,
        Action? onCompleted = null) =>
        _system?.Play(path, volume, pitch, pan, loop, fadeIn, priority, bus, onCompleted) ?? VoiceHandle.Invalid;

    /// <summary>
    /// Loads a clip from a content path off the calling thread, then plays it
    /// (short SFX). Equivalent to <c>Play(await AudioClip.LoadAsync(path), ...)</c>.
    /// </summary>
    public static async Task<VoiceHandle> PlayAsync(
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
        var system = _system;
        if (system is null)
            return VoiceHandle.Invalid;

        var clip = await AudioClip.LoadAsync(path).ConfigureAwait(false);
        return system.Play(clip, volume, pitch, pan, loop, fadeIn, priority, bus, onCompleted);
    }

    /// <summary>Plays long music (stream) and returns its handle.</summary>
    public static VoiceHandle Play(
        AudioStream stream,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Music,
        Action? onCompleted = null) =>
        _system?.Play(stream, volume, pitch, pan, loop, fadeIn, priority, bus, onCompleted) ?? VoiceHandle.Invalid;

    /// <summary>Plays a spatialized clip at <paramref name="position"/>.</summary>
    public static VoiceHandle Play3D(
        AudioClip clip,
        Vector3 position,
        float volume = 1f,
        float pitch = 1f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx,
        Action? onCompleted = null) =>
        _system?.Play3D(clip, position, volume, pitch, loop, fadeIn, priority, bus, onCompleted) ?? VoiceHandle.Invalid;

    /// <summary>Sets the master bus volume (linear).</summary>
    public static void SetMasterVolume(float volume) => _system?.SetBusVolume(AudioBusName.Master, volume);

    /// <summary>Sets the linear volume of a bus.</summary>
    public static void SetBusVolume(AudioBusName name, float volume) => _system?.SetBusVolume(name, volume);

    /// <summary>Returns a bus (meters, mute/solo, effects).</summary>
    public static AudioBus? GetBus(AudioBusName name) => _system?.GetBus(name);

    /// <summary>Stops every voice.</summary>
    public static void StopAll() => _system?.StopAll();

    /// <summary>Available playback devices (empty without a backend).</summary>
    public static IReadOnlyList<AudioDevice> OutputDevices => _system?.Backend?.OutputDevices ?? [];

    /// <summary>Available capture devices (empty without a backend).</summary>
    public static IReadOnlyList<AudioDevice> InputDevices => _system?.Backend?.InputDevices ?? [];

    /// <summary>Records the master output ("record what you hear") to a project file.</summary>
    public static void StartOutputRecording(string path) => _system?.Recorder.StartOutput(path);

    /// <summary>Records the microphone to a project file.</summary>
    public static void StartInputRecording(string path, string? device = null) => _system?.Recorder.StartInput(path, device);

    /// <summary>Stops the current recording.</summary>
    public static void StopRecording() => _system?.Recorder.Stop();

    /// <summary>Starts in-memory microphone capture (see <see cref="Microphone"/>).</summary>
    public static bool StartMicrophone(string? device = null, float bufferSeconds = 5f) =>
        _system?.Microphone.Start(device, bufferSeconds) ?? false;

    /// <summary>Stops microphone capture (the buffer stays readable).</summary>
    public static void StopMicrophone() => _system?.Microphone.Stop();

    /// <summary>Snapshot of the last captured moments, ready to replay via <see cref="Play(AudioClip, float, float, float, bool, float, int, AudioBusName)"/>.</summary>
    public static AudioClip? MicrophoneClip(float seconds = 0f) => _system?.Microphone.TakeClip(seconds);

    /// <summary>True during a recording (output or microphone).</summary>
    public static bool IsRecording => _system?.Recorder is { } recorder && (recorder.IsRecordingOutput || recorder.IsRecordingInput);
}
