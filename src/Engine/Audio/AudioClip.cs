using Crowbar.FileSystems;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Short sound fully decoded in memory, ready to be played by the mixer. It is
/// the audio equivalent of <see cref="Texture2D"/>: a file asset loaded through
/// the global <see cref="Global.ResourceLibrary"/> (canonical path, sharing by
/// path, <see cref="Invalidate"/> for hot reload) and a pure CPU
/// representation, with no backend dependency.
///
/// Files are decoded on open (they are short by nature; long music goes through
/// <see cref="AudioStream"/>). Channels are normalized to interleaved stereo
/// <see cref="float"/>.
/// </summary>
[AssetType("wav", "aiff", "ogg", "mp3")]
public sealed class AudioClip : ResourceFile
{
    /// <summary>Maximum number of min/max buckets in the waveform <see cref="Envelope"/>.</summary>
    public const int MaxEnvelopeBuckets = 1024;

    /// <summary>
    /// The content path the clip came from, or null for a clip created in code
    /// (<see cref="Create"/>). Identity of the shared cache. It is a facade
    /// over the inherited <see cref="ResourceFile.Path"/>, which is empty for
    /// created clips.
    /// </summary>
    public string? ResourcePath => string.IsNullOrEmpty(Path) ? null : Path;

    public string Name { get; private set; } = string.Empty;
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int Frames { get; private set; }

    /// <summary>
    /// Interleaved stereo samples (length = <see cref="Frames"/> x 2). Hides
    /// the base <see cref="ResourceFile.Data"/> stream, which has no meaning
    /// for a clip — the samples <em>are</em> the content, decoded eagerly.
    /// </summary>
    public new float[] Data { get; private set; } = [];

    /// <summary>Number of (min, max) buckets in <see cref="Envelope"/>.</summary>
    public int EnvelopeBucketCount { get; private set; }

    private float[] _envelope = [];

    public TimeSpan Duration => TimeSpan.FromSeconds(Frames / (double)SampleRate);

    /// <summary>
    /// Precomputed min/max waveform envelope, one (min, max) pair per bucket.
    /// Computed once at decode time so a waveform or a peak meter can be drawn
    /// in O(buckets) without scanning the samples every frame.
    /// </summary>
    public ReadOnlySpan<float> Envelope => _envelope.AsSpan(0, EnvelopeBucketCount * 2);

    /// <summary>Allocated by the library, then populated through <see cref="Load"/>.</summary>
    private AudioClip()
    {
    }

    private AudioClip(string name, string? resourcePath, int sampleRate, int channels, float[] data)
    {
        Name = name;
        if (resourcePath is not null)
            Path = resourcePath;
        SampleRate = sampleRate;
        Channels = channels;
        Data = data;
        Frames = data.Length / channels;
        EnvelopeBucketCount = Frames == 0 ? 0 : Math.Min(MaxEnvelopeBuckets, Frames);
        _envelope = ComputeEnvelope(data, channels, Frames, EnvelopeBucketCount);
    }

    /// <summary>
    /// Opens an audio file (WAV, AIFF, ...) and decodes it fully. The same path
    /// always returns the same instance (decoded once).
    /// </summary>
    /// <summary>
    /// Opens an audio file (WAV, AIFF, ...) and decodes it fully, sharing the
    /// instance across every load of the same path through the global
    /// <see cref="Global.ResourceLibrary"/> cache.
    /// </summary>
    public static AudioClip Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ResourceLibrary.Load<AudioClip>(path);
    }

    /// <summary>
    /// Asynchronously loads an audio file: decoding runs off the calling thread
    /// and the instance is installed in the shared cache.
    /// </summary>
    public static Task<AudioClip> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ResourceLibrary.LoadAsync(path, token => CreateFromFile(path, token), cancellationToken);
    }

    /// <summary>Allocates a clip and decodes it into the instance, for the off-thread async path.</summary>
    private static AudioClip CreateFromFile(string path, CancellationToken token)
    {
        var clip = new AudioClip();
        clip.Path = path;
        clip.LoadInto(token);
        return clip;
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
    public static void Invalidate(string path) => ResourceLibrary.Invalidate<AudioClip>(path);

    /// <summary>Drops every cached clip.</summary>
    public static void ClearCache() => ResourceLibrary.Clear<AudioClip>();

    /// <summary>Registers a retaining reference for a clip loaded from a file.</summary>
    public void Retain()
    {
        if (ResourcePath is not null)
            ResourceLibrary.Retain<AudioClip>(ResourcePath);
    }

    /// <summary>Releases a retaining reference (the entry is dropped on the last release).</summary>
    public void Release()
    {
        if (ResourcePath is not null)
            ResourceLibrary.Release<AudioClip>(ResourcePath);
    }

    internal static int CachedCount => ResourceLibrary.CachedCount<AudioClip>();

    /// <summary>
    /// Merges <paramref name="clips"/> into one stereo clip (same sample rate).
    /// Use <see cref="Resample"/> to align rates first.
    /// </summary>
    public static AudioClip Concat(params AudioClip[] clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (clips.Length == 0)
            throw new ArgumentException("At least one clip is required.", nameof(clips));

        var rate = clips[0].SampleRate;
        var totalFrames = 0;
        foreach (var clip in clips)
        {
            ArgumentNullException.ThrowIfNull(clip);
            if (clip.SampleRate != rate)
                throw new ArgumentException($"Clip '{clip.Name}' has a different sample rate ({clip.SampleRate} vs {rate}). Use Resample first.", nameof(clips));
            totalFrames += clip.Frames;
        }

        var data = new float[totalFrames * 2];
        var offset = 0;
        foreach (var clip in clips)
        {
            clip.Data.AsSpan().CopyTo(data.AsSpan(offset));
            offset += clip.Data.Length;
        }
        return new AudioClip(clips[0].Name, null, rate, 2, data);
    }

    /// <summary>Mixes the clip to mono (average of both channels), returned as stereo.</summary>
    public static AudioClip ToMono(AudioClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var data = new float[clip.Data.Length];
        for (var i = 0; i < clip.Frames; i++)
        {
            var value = (clip.Data[i * 2] + clip.Data[i * 2 + 1]) * 0.5f;
            data[i * 2] = value;
            data[i * 2 + 1] = value;
        }
        return new AudioClip(clip.Name, null, clip.SampleRate, 2, data);
    }

    /// <summary>Scales the clip so its peak absolute sample equals <paramref name="peak"/>.</summary>
    public static AudioClip Normalize(AudioClip clip, float peak = 0.9f)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!(peak > 0f))
            throw new ArgumentOutOfRangeException(nameof(peak));

        var max = 0f;
        foreach (var sample in clip.Data)
        {
            var magnitude = MathF.Abs(sample);
            if (magnitude > max)
                max = magnitude;
        }
        if (max <= 1e-6f)
            return clip;

        var scale = peak / max;
        var data = new float[clip.Data.Length];
        for (var i = 0; i < data.Length; i++)
            data[i] = clip.Data[i] * scale;
        return new AudioClip(clip.Name, null, clip.SampleRate, 2, data);
    }

    /// <summary>Linear-interpolation resample to <paramref name="newSampleRate"/>.</summary>
    public static AudioClip Resample(AudioClip clip, int newSampleRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (newSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(newSampleRate));
        if (newSampleRate == clip.SampleRate)
            return clip;

        var ratio = clip.SampleRate / (double)newSampleRate;
        var frames = Math.Max(1, (int)(clip.Frames * (newSampleRate / (double)clip.SampleRate)));
        var data = new float[frames * 2];

        for (var i = 0; i < frames; i++)
        {
            var position = i * ratio;
            var floor = (int)position;
            var fraction = (float)(position - floor);
            var next = Math.Min(floor + 1, clip.Frames - 1);
            for (var c = 0; c < 2; c++)
            {
                var a = clip.Data[floor * 2 + c];
                var b = clip.Data[next * 2 + c];
                data[i * 2 + c] = a + (b - a) * fraction;
            }
        }
        return new AudioClip(clip.Name, null, newSampleRate, 2, data);
    }

    private static float[] ComputeEnvelope(float[] data, int channels, int frames, int buckets)
    {
        var envelope = new float[buckets * 2];
        if (frames == 0)
            return envelope;

        // Frame-accurate bucket bounds (double avoids drift for large clips).
        var framesPerBucket = frames / (double)buckets;
        for (var b = 0; b < buckets; b++)
        {
            var start = (int)(b * framesPerBucket);
            var end = (int)((b + 1) * framesPerBucket);
            if (end <= start)
                end = start + 1;
            if (end > frames)
                end = frames;

            var min = float.PositiveInfinity;
            var max = float.NegativeInfinity;
            for (var i = start; i < end; i++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var value = data[i * channels + c];
                    if (value < min)
                        min = value;
                    if (value > max)
                        max = value;
                }
            }

            if (min > max)
            {
                min = 0f;
                max = 0f;
            }

            envelope[b * 2] = min;
            envelope[b * 2 + 1] = max;
        }

        return envelope;
    }

    /// <summary>
    /// Decodes the file at <see cref="ResourceFile.Path"/> into this instance.
    /// The library allocates the clip, assigns its path and calls this; loading
    /// the same path twice returns the same instance through the shared cache.
    /// </summary>
    public override void Load() => LoadInto(CancellationToken.None);

    private void LoadInto(CancellationToken cancellationToken)
    {
        if (!FileSystem.Content.FileExists(Path))
            throw new FileNotFoundException("Audio file not found.", Path);

        var data = FileSystem.Content.ReadAllBytes(Path);
        using var decoder = AudioDecoderFactory.CreateFromBytes(Path, data);

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
            throw new InvalidDataException($"The audio file '{Path}' contains no samples.");

        Name = PathUtil.GetFileNameWithoutExtension(Path);
        SampleRate = decoder.SampleRate;
        Channels = 2;
        Data = buffer.ToArray();
        Frames = Data.Length / Channels;
        EnvelopeBucketCount = Frames == 0 ? 0 : Math.Min(MaxEnvelopeBuckets, Frames);
        _envelope = ComputeEnvelope(Data, Channels, Frames, EnvelopeBucketCount);
        IsValid = true;
    }
}
