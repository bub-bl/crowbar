namespace Crowbar.Engine.Audio;

/// <summary>Un périphérique audio énuméré (lecture ou capture).</summary>
public readonly record struct AudioDevice(string Name, bool IsDefault);

/// <summary>
/// Flux de capture audio (micro) ouvert par un <see cref="IAudioBackend"/> : il
/// fournit des frames <see cref="float"/> stéréo (mono dupliqué par le backend)
/// via <see cref="Read"/>, dans un buffer réutilisé par l'appelant.
/// </summary>
public interface IAudioCaptureDevice : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>
    /// Défile jusqu'à <c>destination.Length / 2</c> frames capturées ; retourne
    /// le nombre de frames lues (0 si rien n'est encore disponible).
    /// </summary>
    int Read(Span<float> destination);

    /// <summary>Démarre la capture (une fois le device ouvert).</summary>
    void Start();
}

/// <summary>
/// Backend audio : l'unique couche qui parle à la plateforme. L'implémentation
/// actuelle (<see cref="SdlAudioBackend"/>) utilise l'API de poussée SDL2
/// (<c>SDL_OpenAudioDevice</c> + <c>SDL_QueueAudio</c>). La migration vers SDL3
/// (<c>SDL_PutAudioStreamData</c> + streams) ne modifiera que cette classe, pas
/// le runtime (<see cref="AudioSystem"/>) ni les effets.
///
/// Le runtime pousse des blocs stéréo <see cref="float"/> entrelacés et ne
/// touche jamais aux buffers natifs.
/// </summary>
public interface IAudioBackend : IDisposable
{
    string BackendName { get; }

    /// <summary>Fréquence d'échantillonnage effective du périphérique de sortie.</summary>
    int SampleRate { get; }

    /// <summary>Nombre de canaux effectifs de la sortie (le runtime produit du stéréo).</summary>
    int Channels { get; }

    /// <summary>Périphériques de lecture disponibles.</summary>
    IReadOnlyList<AudioDevice> OutputDevices { get; }

    /// <summary>Périphériques de capture disponibles.</summary>
    IReadOnlyList<AudioDevice> InputDevices { get; }

    /// <summary>Vrai tant que le backend peut accepter des blocs.</summary>
    bool IsRunning { get; }

    /// <summary>Nombre d'échantillons encore en file dans le périphérique (latence).</summary>
    uint QueuedSamples { get; }

    /// <summary>Pousse un bloc stéréo entrelacé. Retourne false en cas d'échec.</summary>
    bool QueueSamples(ReadOnlySpan<float> interleaved);

    /// <summary>Démarre la lecture (sort le périphérique de pause).</summary>
    void Start();

    /// <summary>Met la lecture en pause.</summary>
    void Stop();

    /// <summary>Ouvre un périphérique de capture (micro), ou null si indisponible.</summary>
    IAudioCaptureDevice? OpenCapture(string? device);
}
