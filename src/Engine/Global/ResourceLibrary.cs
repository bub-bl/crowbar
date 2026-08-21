using Crowbar.Engine.Audio;

namespace Crowbar.Engine.Global;

/// <summary>
/// The resource loading and caching API — the s&amp;box-style
/// <c>ResourceLibrary</c>. Exposed globally as
/// <see cref="GlobalNamespaces.ResourceLibrary"/>. It is the single owner of
/// the engine's per-type resource caches: instead of every resource class
/// carrying its own static <see cref="ResourceCache{T}"/>, the caches live here,
/// keyed by type, and the classes' <c>Load</c> statics delegate to it. Content
/// code never repeats the try/catch dance — <see cref="LoadModel"/> substitutes
/// <see cref="Model.Error"/> for an unreadable model.
///
/// Types register their loader (and optional released callback) with
/// <see cref="RegisterLoader{T}"/>; the engine registers its file-backed
/// resources (Model, Texture2D, AudioClip, Shader) in the constructor, and
/// custom resource types inheriting <see cref="ResourceFile"/> register theirs.
/// Every cached resource is shared by path and reference-counted
/// (<see cref="Retain{T}"/>/<see cref="Release{T}"/>), so the renderer keeps
/// exactly one GPU copy per asset.
/// </summary>
public sealed class ResourceLibrary
{
    private readonly Dictionary<Type, ResourceCache<ResourceFile>> _caches = [];
    private readonly Dictionary<Type, Func<string, ResourceFile>> _loaders = [];
    private readonly Dictionary<Type, Action<ResourceFile>> _released = [];
    private readonly Lock _lock = new();

    public ResourceLibrary()
    {
        RegisterLoader<Model>(Model.Import, static model => model.ReleaseResources());
        RegisterLoader<Texture2D>(Texture2D.Import);
        RegisterLoader<AudioClip>(AudioClip.Import);
        RegisterLoader<Shader>(Shader.Import);
    }

    /// <summary>
    /// Registers the loader used to load a resource type by path, and the
    /// optional callback run when the cache discards an entry of that type.
    /// The engine registers its file-backed types; custom
    /// <see cref="ResourceFile"/> subclasses register theirs.
    /// </summary>
    public void RegisterLoader<T>(Func<string, T> loader, Action<T>? released = null) where T : ResourceFile
    {
        ArgumentNullException.ThrowIfNull(loader);

        lock (_lock)
        {
            _loaders[typeof(T)] = path => loader(path);
            _released[typeof(T)] = released is null ? _ => { } : resource => released((T)resource);
        }
    }

    /// <summary>Loads a resource of type <typeparamref name="T"/> by path (cached, shared by path). Throws on failure.</summary>
    public T Load<T>(string path) where T : ResourceFile
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return (T)Cache<T>().Load(path);
    }

    /// <summary>Asynchronously loads a resource of type <typeparamref name="T"/> (shared cache, in-flight dedup).</summary>
    public async Task<T> LoadAsync<T>(string path, Func<CancellationToken, T> load, CancellationToken cancellationToken = default)
        where T : ResourceFile
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(load);
        return (T)await Cache<T>().LoadAsync(path, token => load(token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the cached resource at <paramref name="path"/> without loading it.</summary>
    public bool TryGet<T>(string path, out T? resource) where T : ResourceFile
    {
        if (Cache<T>().TryGet(path, out var cached) && cached is T typed)
        {
            resource = typed;
            return true;
        }

        resource = null;
        return false;
    }

    /// <summary>Every cached resource of type <typeparamref name="T"/>.</summary>
    public IReadOnlyList<T> GetAll<T>() where T : ResourceFile =>
        Cache<T>().Snapshot().OfType<T>().ToArray();

    /// <summary>Records a holder reference for the cached entry at <paramref name="path"/>.</summary>
    public void Retain<T>(string path) where T : ResourceFile => Cache<T>().Retain(path);

    /// <summary>Drops a holder reference; the entry is discarded when the last holder releases.</summary>
    public void Release<T>(string path) where T : ResourceFile => Cache<T>().Release(path);

    /// <summary>Discards the cached entry at <paramref name="path"/> so the next load re-reads it.</summary>
    public void Invalidate<T>(string path) where T : ResourceFile => Cache<T>().Invalidate(path);

    /// <summary>Discards every cached entry of type <typeparamref name="T"/>.</summary>
    public void Clear<T>() where T : ResourceFile => Cache<T>().Clear();

    /// <summary>Number of holders recorded for the entry at <paramref name="path"/>.</summary>
    public int GetReferenceCount<T>(string path) where T : ResourceFile => Cache<T>().GetReferenceCount(path);

    /// <summary>Number of cached entries of type <typeparamref name="T"/>.</summary>
    public int CachedCount<T>() where T : ResourceFile => Cache<T>().Count;

    /// <summary>
    /// Loads a model by path, or <see cref="Model.Error"/> when it cannot be
    /// loaded. A warning is logged; the failure itself is not cached, so the
    /// next call re-attempts (content that may appear later keeps working).
    /// </summary>
    public Model LoadModel(string path)
    {
        try
        {
            return Load<Model>(path);
        }
        catch (Exception ex)
        {
            GlobalNamespaces.Log.Warn($"[Resource] Failed to load model '{path}': {ex.Message}");
            return Model.Error;
        }
    }

    private ResourceCache<ResourceFile> Cache<T>() where T : ResourceFile
    {
        lock (_lock)
        {
            if (_caches.TryGetValue(typeof(T), out var cache))
                return cache;

            if (!_loaders.TryGetValue(typeof(T), out var loader))
                throw new InvalidOperationException(
                    $"No loader registered for resource type '{typeof(T).Name}'. " +
                    "Register one with RegisterLoader before loading it.");

            cache = new ResourceCache<ResourceFile>(
                loader,
                _released.TryGetValue(typeof(T), out var released) ? released : null);
            _caches[typeof(T)] = cache;
            return cache;
        }
    }
}
