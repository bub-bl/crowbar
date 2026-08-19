using Crowbar.FileSystems;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Son court entièrement décodé en mémoire, prêt à être joué par le mixer.
/// C'est l'équivalent audio de <see cref="Texture2D"/> : un asset fichier chargé
/// via le <see cref="ResourceCache{T}"/> global (chemin canonique, partage par
/// chemin, <see cref="Invalidate"/> pour le rechargement à chaud) et une
/// représentation CPU pure, sans aucune dépendance au backend.
///
/// Les fichiers sont décodés à l'ouverture (ils sont courts par nature ; la
/// musique longue passe par <see cref="AudioStream"/>). Les canaux sont
/// normalisés en stéréo <see cref="float"/> entrelacé.
/// </summary>
public sealed class AudioClip
{
    internal static readonly ResourceCache<AudioClip> Cache = new(CreateLoaded);

    /// <summary>
    /// Le chemin de contenu d'où provient le clip, ou null pour un clip créé en
    /// code (<see cref="Create"/>). Identité du cache partagé.
    /// </summary>
    public string? ResourcePath { get; }

    public string Name { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int Frames { get; }

    /// <summary>Échantillons stéréo entrelacés (length = <see cref="Frames"/> × 2).</summary>
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
    /// Ouvre un fichier audio (WAV, AIFF, ...) et le décode intégralement. Le
    /// même chemin renvoie toujours la même instance (décodé une fois).
    /// </summary>
    public static AudioClip Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Cache.Load(path);
    }

    /// <summary>
    /// Charge asynchrone d'un fichier audio : le décodage s'exécute hors du
    /// thread appelant et l'instance est installée dans le cache partagé.
    /// </summary>
    public static Task<AudioClip> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Cache.LoadAsync(path, token => DecodeFile(path, token), cancellationToken);
    }

    /// <summary>
    /// Crée un clip depuis des échantillons <see cref="float"/> entrelacés.
    /// Mono et stéréo sont acceptés ; le contenu est copié et normalisé en
    /// stéréo. Utile pour les sons procéduraux (test-tone, UI, ...).
    /// </summary>
    public static AudioClip Create(string name, int sampleRate, ReadOnlySpan<float> interleaved, int channels = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), "Mono (1) ou stéréo (2) uniquement.");
        if (interleaved.Length % channels != 0)
            throw new ArgumentException("Le nombre d'échantillons n'est pas un multiple du nombre de canaux.", nameof(interleaved));

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

    /// <summary>Discarde le clip en cache à <paramref name="path"/> pour le re-décoder au prochain chargement.</summary>
    public static void Invalidate(string path) => Cache.Invalidate(path);

    /// <summary>Discarde tous les clips en cache.</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>Enregistre une référence conservatrice pour un clip chargé depuis un fichier.</summary>
    public void Retain()
    {
        if (ResourcePath is not null)
            Cache.Retain(ResourcePath);
    }

    /// <summary>Libère une référence conservatrice (l'entrée est jetée à la dernière libération).</summary>
    public void Release()
    {
        if (ResourcePath is not null)
            Cache.Release(ResourcePath);
    }

    internal static int CachedCount => Cache.Count;

    private static AudioClip CreateLoaded(string path)
    {
        if (!FileSystem.Content.FileExists(path))
            throw new FileNotFoundException("Fichier audio introuvable.", path);

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
            throw new InvalidDataException($"Le fichier audio '{path}' ne contient aucun échantillon.");

        return new AudioClip(
            PathUtil.GetFileNameWithoutExtension(path),
            path,
            decoder.SampleRate,
            2,
            buffer.ToArray());
    }
}
