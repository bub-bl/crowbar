using Crowbar.FileSystems;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Decoder factory: picks the implementation from the file's content (magic
/// bytes), falling back to the extension. WAV, AIFF, Ogg/Vorbis and MP3 are
/// available; FLAC is still deferred (dr_flac requires a native single-file C
/// binding and per-platform binaries).
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

    /// <summary>Opens a decoder over already-loaded bytes, based on their content.</summary>
    public static IAudioDecoder CreateFromBytes(string path, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var format = DetectFormat(data);
        if (format == AudioFileFormat.Unknown)
            format = DetectFormatFromExtension(path);

        return format switch
        {
            AudioFileFormat.Wav => new WavDecoder(data),
            AudioFileFormat.Aiff => new AiffDecoder(data),
            AudioFileFormat.OggVorbis => new VorbisDecoder(data),
            AudioFileFormat.Mp3 => new Mp3Decoder(data),
            AudioFileFormat.Flac => throw new NotSupportedException(
                "FLAC is deferred: dr_flac requires a native single-file C binding and per-platform binaries."),
            _ => throw new NotSupportedException(
                $"Unsupported audio format: {(new FilePath(path).GetExtensionWithDot() ?? "<unknown>")}")
        };
    }

    private enum AudioFileFormat
    {
        Unknown,
        Wav,
        Aiff,
        OggVorbis,
        Mp3,
        Flac
    }

    private static AudioFileFormat DetectFormat(byte[] data)
    {
        if (data.Length >= 12 && Ascii(data, 0) == "RIFF" && Ascii(data, 8) == "WAVE")
            return AudioFileFormat.Wav;
        if (data.Length >= 12 && Ascii(data, 0) == "FORM" && Ascii(data, 8) == "AIFF")
            return AudioFileFormat.Aiff;
        if (data.Length >= 4 && Ascii(data, 0) == "fLaC")
            return AudioFileFormat.Flac;
        if (data.Length >= 4 && Ascii(data, 0) == "OggS")
            return AudioFileFormat.OggVorbis;
        if (data.Length >= 3 && Ascii(data, 0) == "ID3")
            return AudioFileFormat.Mp3;
        if (data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0)
            return AudioFileFormat.Mp3;
        return AudioFileFormat.Unknown;
    }

    private static AudioFileFormat DetectFormatFromExtension(string path)
    {
        var extension = new FilePath(path).GetExtensionWithDot()?.ToLowerInvariant() ?? string.Empty;
        return extension switch
        {
            ".wav" or ".wave" => AudioFileFormat.Wav,
            ".aiff" or ".aif" => AudioFileFormat.Aiff,
            ".ogg" or ".oga" => AudioFileFormat.OggVorbis,
            ".mp3" => AudioFileFormat.Mp3,
            ".flac" => AudioFileFormat.Flac,
            _ => AudioFileFormat.Unknown
        };
    }

    private static string Ascii(byte[] data, int offset) =>
        $"{(char)data[offset]}{(char)data[offset + 1]}{(char)data[offset + 2]}{(char)data[offset + 3]}";
}
