using Crowbar.FileSystems;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Short sound fully decoded in memory, ready to be played by the mixer. It is
/// the audio equivalent of <see cref="Texture2D"/>: a file asset loaded through
/// the global <see cref="ResourceCache{T}"/> (canonical path, sharing by path,
/// <see cref="Invalidate"/> for hot reload) and a pure CPU representation, with
/// no backend dependency.
///
/// Files are decoded on open (they are short by nature; long music goes through
/// <see cref="AudioStream"/>). Channels are normalized to interleaved stereo
/// <see cref="float"/>.
/// </summary>
public sealed class AudioClip
{
    internal static readonly ResourceCache<AudioClip> Cache = new(CreateLoaded);

    /// <summary>
    /// The content path the clip came from, or null for a clip created in code
    /// (<see cref="Create"/>). Identity of the shared cache.
    /// </summary>
    public string? ResourcePath { get; }

    public string Name { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int Frames { get; }

    /// <summary>Interleaved stereo samples (length = <see cref="Frames"/> x 2).</summary>
    public float[] Data { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds(Frames / (double)SampleRate);

    private AudioClip(string name, string? resourcePath, int sampleRate, int channels, float[] data)
    {
        Name = name;
        ResourcePath = resourcePath;
        SampleRate = sampleRate;
        Channels = channels;
        Data = data;
        Frames = data.Length / channels;
    }

    /// <summary>
    /// Opens an audio file (WAV, AIFF, ...) and decodes it fully. The same path
    /// always returns the same instance (decoded once).
    /// </summary>
    public static AudioClip Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Cache.Load(path);
    }

    /// <summary>
    /// Asynchronously loads an audio file: decoding runs off the calling thread
    /// and the instance is installed in the shared cache.
    /// </summary>
    public static Task<AudioClip> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Cache.LoadAsync(path, token => DecodeFile(path, token), cancellationToken);
    }

    /// <summary>
    /// Creates a clip from interleaved <see cref="float"/> samples. Mono and
    /// stereo are accepted; the content is copied and normalized to stereo.
    /// Useful for procedural sounds (test tone, UI, ...).
    /// </summary>
    public static AudioClip Create(string name, int sampleRate, ReadOnlySpan<float> interleaved, int channels = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), "Mono (1) or stereo (2) only.");
        if (interleaved.Length % channels != 0)
            throw new ArgumentException("The sample count is not a multiple of the channel count.", nameof(interleaved));

        var frames = interleaved.Length / channels;
        var data = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var left = interleaved[i * channels];
            var right = channels == 2 ? interleaved[i * channels + 1] : left;
            data[i * 2] = left;
            data[i * 2 + 1] = right;
        }

        return new AudioClip(name, null, sampleRate, 2, data);
    }

    /// <summary>Drops the cached clip at <paramref name="path"/> so it is re-decoded on next load.</summary>
    public static void Invalidate(string path) => Cache.Invalidate(path);

    /// <summary>Drops every cached clip.</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>Registers a retaining reference for a clip loaded from a file.</summary>
    public void Retain()
    {
        if (ResourcePath is not null)
            Cache.Retain(ResourcePath);
    }

    /// <summary>Releases a retaining reference (the entry is dropped on the last release).</summary>
    public void Release()
    {
        if (ResourcePath is not null)
            Cache.Release(ResourcePath);
    }

    internal static int CachedCount => Cache.Count;

    private static AudioClip CreateLoaded(string path)
    {
        if (!FileSystem.Content.FileExists(path))
            throw new FileNotFoundException("Audio file not found.", path);

        return DecodeFile(path, CancellationToken.None);
    }

    private static AudioClip DecodeFile(string path, CancellationToken cancellationToken)
    {
        var data = FileSystem.Content.ReadAllBytes(path);
        using var decoder = AudioDecoderFactory.CreateFromBytes(path, data);

        var buffer = new List<float>((int)Math.Min(decoder.TotalFrames, 1 << 20) * 2 + 2);
        var scratch = new float[4096 * 2];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = decoder.Read(scratch);
            if (read <= 0)
                break;
            buffer.AddRange(scratch.AsSpan(0, read * 2).ToArray());
        }

        if (buffer.Count == 0)
            throw new InvalidDataException($"The audio file '{path}' contains no samples.");

        return new AudioClip(
            PathUtil.GetFileNameWithoutExtension(path),
            path,
            decoder.SampleRate,
            2,
            buffer.ToArray());
    }
}
