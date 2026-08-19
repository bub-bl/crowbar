namespace Crowbar.Engine.Audio;

/// <summary>
/// Compteur d'échantillons <see cref="long"/> monotone qui sert de base de temps
/// au moteur audio : c'est lui qui relie le rendu temps réel, le rendu hors
/// ligne (tests/export) et la future synchronisation image/audio. Le thread DSP
/// l'avance de <c>frames</c> échantillons à chaque bloc rendu ; le thread de jeu
/// ne fait que le lire.
/// </summary>
public sealed class AudioClock
{
    /// <summary>Fréquence d'échantillonnage du moteur (Hz).</summary>
    public int SampleRate { get; }

    private long _samples;

    public AudioClock(int sampleRate)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
    }

    /// <summary>Position courante, en échantillons (frames) depuis le démarrage.</summary>
    public long PositionSamples => Interlocked.Read(ref _samples);

    /// <summary>Position courante, en secondes.</summary>
    public double TimeSeconds => PositionSamples / (double)SampleRate;

    /// <summary>
    /// Avance le compteur de <paramref name="frames"/> échantillons. Appelé
    /// uniquement par le thread DSP (ou par un rendu hors ligne).
    /// </summary>
    public void Advance(int frames) => Interlocked.Add(ref _samples, frames);

    /// <summary>Remet le compteur à zéro.</summary>
    public void Reset() => Interlocked.Exchange(ref _samples, 0);
}
