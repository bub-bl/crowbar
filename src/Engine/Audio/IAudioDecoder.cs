namespace Crowbar.Engine.Audio;

/// <summary>
/// Décodeur audio maison : il produit des échantillons <see cref="float"/> en
/// stéréo entrelacé à partir d'un buffer d'octets chargé une seule fois. Le
/// décodeur garde sa propre position de lecture, donc le thread DSP ne relit
/// jamais un flux système : <see cref="Read"/> ne doit rien allouer.
///
/// Les implémentations fournies sont <see cref="WavDecoder"/> (RIFF/WAVE) et
/// <see cref="AiffDecoder"/> (FORM/AIFF). OGG/Vorbis (NVorbis) et MP3 (NLayer)
/// se brancheront ici plus tard, derrière la même interface — voir
/// <see cref="AudioDecoderFactory"/>.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>Fréquence d'échantillonnage (Hz).</summary>
    int SampleRate { get; }

    /// <summary>Nombre de canaux du flux (le moteur normalise en stéréo).</summary>
    int Channels { get; }

    /// <summary>Nombre total de frames, ou <c>-1</c> quand il est inconnu.</summary>
    long TotalFrames { get; }

    /// <summary>Position courante, en frames.</summary>
    long Position { get; }

    /// <summary>
    /// Décode jusqu'à <c>destination.Length / 2</c> frames en stéréo entrelacé.
    /// Retourne le nombre de frames effectivement décodées (0 en fin de flux).
    /// </summary>
    int Read(Span<float> destination);

    /// <summary>Repositionne la lecture à <paramref name="frame"/>.</summary>
    void Seek(long frame);
}
