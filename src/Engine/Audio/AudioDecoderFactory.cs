using Crowbar.FileSystems;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Decoder factory: picks the implementation based on the file extension. WAV
/// and AIFF are hand-written (pure C#). OGG/Vorbis (NVorbis) and MP3 (NLayer),
/// both MIT-licensed, will plug in here without changing the
/// <see cref="IAudioDecoder"/> interface — deliberately deferred until the
/// NuGet packages are verified/restored. FLAC is deferred (dr_flac requires a
/// non-trivial single-file C hand-binding).
/// </summary>
public static class AudioDecoderFactory
{
    /// <summary>
    /// Opens a decoder over the full contents of <paramref name="path"/> (the
    /// bytes are loaded once; decoding stays allocation-free on the DSP thread).
    /// </summary>
    public static IAudioDecoder Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return CreateFromBytes(path, FileSystem.Content.ReadAllBytes(path));
    }

    /// <summary>Opens a decoder over already-loaded bytes, based on the extension.</summary>
    public static IAudioDecoder CreateFromBytes(string path, byte[] data)
    {
        var extension = new FilePath(path).GetExtensionWithDot() ?? string.Empty;
        return extension.ToLowerInvariant() switch
        {
            ".wav" or ".wave" => new WavDecoder(data),
            ".aiff" or ".aif" => new AiffDecoder(data),
            ".ogg" or ".oga" => throw new NotSupportedException(
                "OGG/Vorbis (NVorbis) is not integrated yet: add the NVorbis package, then plug a decoder into AudioDecoderFactory."),
            ".mp3" => throw new NotSupportedException(
                "MP3 (NLayer) is not integrated yet: add the NLayer package, then plug a decoder into AudioDecoderFactory."),
            ".flac" => throw new NotSupportedException(
                "FLAC is deferred (dr_flac requires a single-file C hand-binding)."),
            _ => throw new NotSupportedException($"Unsupported audio format: {extension}")
        };
    }
}
