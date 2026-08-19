using Crowbar.FileSystems;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Fabrique de décodeurs : choisit l'implémentation selon l'extension du
/// fichier. WAV et AIFF sont écrits maison (pur C#). OGG/Vorbis (NVorbis) et
/// MP3 (NLayer), tous deux sous licence MIT, se brancheront ici sans changer
/// l'interface <see cref="IAudioDecoder"/> — ajout volontairement différé tant
/// que les paquets NuGet ne sont pas vérifiés/restaurés. FLAC est reporté
/// (dr_flac demande un hand-binding C single-file non trivial).
/// </summary>
public static class AudioDecoderFactory
{
    /// <summary>
    /// Ouvre un décodeur sur le contenu complet du fichier <paramref name="path"/>
    /// (les octets sont chargés une fois, le décodage reste sans allocation sur
    /// le thread DSP).
    /// </summary>
    public static IAudioDecoder Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return CreateFromBytes(path, FileSystem.Content.ReadAllBytes(path));
    }

    /// <summary>Ouvre un décodeur sur des octets déjà chargés, selon l'extension.</summary>
    public static IAudioDecoder CreateFromBytes(string path, byte[] data)
    {
        var extension = new FilePath(path).GetExtensionWithDot() ?? string.Empty;
        return extension.ToLowerInvariant() switch
        {
            ".wav" or ".wave" => new WavDecoder(data),
            ".aiff" or ".aif" => new AiffDecoder(data),
            ".ogg" or ".oga" => throw new NotSupportedException(
                "OGG/Vorbis (NVorbis) n'est pas encore intégré : ajoutez le paquet NVorbis puis branchez un décodeur dans AudioDecoderFactory."),
            ".mp3" => throw new NotSupportedException(
                "MP3 (NLayer) n'est pas encore intégré : ajoutez le paquet NLayer puis branchez un décodeur dans AudioDecoderFactory."),
            ".flac" => throw new NotSupportedException(
                "FLAC est reporté (dr_flac demande un hand-binding C single-file)."),
            _ => throw new NotSupportedException($"Format audio non pris en charge : {extension}")
        };
    }
}
