using System.Numerics;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Façade audio globale, sur le même modèle que <see cref="Crowbar.Engine.InputSystem.Input"/> et
/// <see cref="Crowbar.Engine.InputSystem.Mouse"/> : <c>Audio.Bind(...)</c> une fois au
/// démarrage, puis <c>Audio.Play(clip)</c> depuis n'importe où, y compris un
/// gamemode scripté. Toutes les méthodes sont sûres sans backend lié (elles
/// deviennent des no-op) et renvoient une <see cref="VoiceHandle"/> pour piloter
/// la voix (volume, pitch, pan, fade, position 3D, effets).
/// </summary>
public static class Audio
{
    private static AudioSystem? _system;

    /// <summary>Lie le moteur audio à la façade statique.</summary>
    public static void Bind(AudioSystem system)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
    }

    /// <summary>Détache la façade (appelé quand le moteur est détruit).</summary>
    public static void Unbind() => _system = null;

    /// <summary>Vrai une fois <see cref="Bind"/> appelé.</summary>
    public static bool IsBound => _system is not null;

    /// <summary>L'écouteur 3D (position/orientation pour <see cref="Play3D"/>).</summary>
    public static AudioListener Listener => _system?.Listener ?? throw new InvalidOperationException("Audio n'est pas lié : appelez Audio.Bind(...) au démarrage.");

    /// <summary>Le buffer micro en mémoire (null tant qu'Audio n'est pas lié).</summary>
    public static Microphone? Microphone => _system?.Microphone;

    /// <summary>Joue un clip court (SFX) et retourne sa poignée.</summary>
    public static VoiceHandle Play(
        AudioClip clip,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx) =>
        _system?.Play(clip, volume, pitch, pan, loop, fadeIn, priority, bus) ?? VoiceHandle.Invalid;

    /// <summary>
    /// Charge un clip depuis un chemin de contenu puis le joue (SFX court).
    /// Lève une exception si le fichier est introuvable ; pour une musique
    /// longue, utilisez <see cref="Play(AudioStream, float, float, float, bool, float, int, AudioBusName)"/>.
    /// </summary>
    public static VoiceHandle Play(
        string path,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx) =>
        _system?.Play(path, volume, pitch, pan, loop, fadeIn, priority, bus) ?? VoiceHandle.Invalid;

    /// <summary>Joue une musique longue (stream) et retourne sa poignée.</summary>
    public static VoiceHandle Play(
        AudioStream stream,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Music) =>
        _system?.Play(stream, volume, pitch, pan, loop, fadeIn, priority, bus) ?? VoiceHandle.Invalid;

    /// <summary>Joue un clip spatialisé à <paramref name="position"/>.</summary>
    public static VoiceHandle Play3D(
        AudioClip clip,
        Vector3 position,
        float volume = 1f,
        float pitch = 1f,
        bool loop = false,
        float fadeIn = 0f,
        int priority = 0,
        AudioBusName bus = AudioBusName.Sfx) =>
        _system?.Play3D(clip, position, volume, pitch, loop, fadeIn, priority, bus) ?? VoiceHandle.Invalid;

    /// <summary>Règle le volume du bus master (linéaire).</summary>
    public static void SetMasterVolume(float volume) => _system?.SetBusVolume(AudioBusName.Master, volume);

    /// <summary>Règle le volume linéaire d'un bus.</summary>
    public static void SetBusVolume(AudioBusName name, float volume) => _system?.SetBusVolume(name, volume);

    /// <summary>Retourne un bus (meters, mute/solo, effets).</summary>
    public static AudioBus? GetBus(AudioBusName name) => _system?.GetBus(name);

    /// <summary>Arrête toutes les voix.</summary>
    public static void StopAll() => _system?.StopAll();

    /// <summary>Périphériques de lecture disponibles (vide sans backend).</summary>
    public static IReadOnlyList<AudioDevice> OutputDevices => _system?.Backend?.OutputDevices ?? [];

    /// <summary>Périphériques de capture disponibles (vide sans backend).</summary>
    public static IReadOnlyList<AudioDevice> InputDevices => _system?.Backend?.InputDevices ?? [];

    /// <summary>Enregistre la sortie master (« record ce que tu entends ») vers un fichier projet.</summary>
    public static void StartOutputRecording(string path) => _system?.Recorder.StartOutput(path);

    /// <summary>Enregistre le micro vers un fichier projet.</summary>
    public static void StartInputRecording(string path, string? device = null) => _system?.Recorder.StartInput(path, device);

    /// <summary>Arrête l'enregistrement en cours.</summary>
    public static void StopRecording() => _system?.Recorder.Stop();

    /// <summary>Démarre la capture micro en mémoire (voir <see cref="Microphone"/>).</summary>
    public static bool StartMicrophone(string? device = null, float bufferSeconds = 5f) =>
        _system?.Microphone.Start(device, bufferSeconds) ?? false;

    /// <summary>Arrête la capture micro (le buffer reste lisible).</summary>
    public static void StopMicrophone() => _system?.Microphone.Stop();

    /// <summary>Instantané des derniers instants capturés, prêt à rejouer via <see cref="Play(AudioClip, float, float, float, bool, float, int, AudioBusName)"/>.</summary>
    public static AudioClip? MicrophoneClip(float seconds = 0f) => _system?.Microphone.TakeClip(seconds);

    /// <summary>Vrai pendant un enregistrement (sortie ou micro).</summary>
    public static bool IsRecording => _system?.Recorder is { } recorder && (recorder.IsRecordingOutput || recorder.IsRecordingInput);
}
