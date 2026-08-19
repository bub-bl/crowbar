namespace Crowbar.Engine.Audio;

/// <summary>
/// Contexte d'exécution passé à chaque <see cref="IAudioEffect.Process"/> : il
/// décrit le bloc en cours (format, taille, temps) sans aucune dépendance au
/// backend. Les effets restent ainsi du C# pur, testables hors ligne.
/// </summary>
public readonly struct AudioEffectContext
{
    /// <summary>Fréquence d'échantillonnage (Hz).</summary>
    public readonly int SampleRate;

    /// <summary>Nombre de canaux (le moteur travaille en stéréo entrelacé).</summary>
    public readonly int Channels;

    /// <summary>Nombre de frames (échantillons par canal) dans le buffer.</summary>
    public readonly int Frames;

    /// <summary>Temps audio au début du bloc (secondes), depuis le démarrage du moteur.</summary>
    public readonly double TimeSeconds;

    public AudioEffectContext(int sampleRate, int channels, int frames, double timeSeconds)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Frames = frames;
        TimeSeconds = timeSeconds;
    }
}

/// <summary>
/// Effet DSP maison, en C# pur : il traite un bloc stéréo entrelacé en place.
/// Les implémentations gardent leur état interne (lignes de délai, coefficients,
/// enveloppes) dans des buffers alloués à la construction, afin que
/// <see cref="Process"/> n'alloue rien en régime permanent sur le thread DSP.
///
/// Les paramètres sont des <c>float</c> volatils : leur écriture depuis le
/// thread de jeu est atomique et sans verrou, et l'effet applique le lissage
/// nécessaire pour éviter les artefacts (zipper noise) côté DSP.
/// </summary>
public interface IAudioEffect
{
    /// <summary>Nombre de paramètres exposés par <see cref="GetParameter"/>.</summary>
    int ParameterCount { get; }

    /// <summary>Traite le bloc stéréo entrelacé en place.</summary>
    void Process(Span<float> buffer, AudioEffectContext context);

    /// <summary>Lit la valeur du paramètre <paramref name="index"/>.</summary>
    float GetParameter(int index);

    /// <summary>Écrit la valeur du paramètre <paramref name="index"/> (atomique, sans verrou).</summary>
    void SetParameter(int index, float value);

    /// <summary>Réinitialise l'état interne (lignes de délai, enveloppes, ...).</summary>
    void Reset();
}
