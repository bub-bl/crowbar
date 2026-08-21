using System.Linq.Expressions;
using System.Reflection;
using Crowbar.Engine.Audio;

namespace Crowbar.Engine.Global;

/// <summary>
/// The resource loading and caching API — the s&amp;box-style
/// <c>ResourceLibrary</c>. Exposed globally as
/// <see cref="GlobalNamespaces.ResourceLibrary"/>. It is the single owner of
/// the engine's per-type resource caches: instead of every resource class
/// carrying its own static cache, the caches live here in one
/// <see cref="Dictionary{Type, IResourceCache}"/> keyed by the resource type,
/// and the classes' <c>Load</c> statics delegate to it. Content code never
/// repeats the try/catch dance — <see cref="LoadModel"/> substitutes
/// <see cref="Model.Error"/> for an unreadable model.
///
/// Which assemblies participate is decided by the composition root, not by
/// this library: each assembly's resource types are registered with
/// <see cref="Register"/>. The application host registers the engine's own
/// types (Model, Texture2D, AudioClip, Shader) at startup, the editor registers
/// its assembly, and the game project registers its loaded assembly.
/// A type participates by marking itself <see cref="AssetTypeAttribute"/>; the
/// library allocates each instance, assigns its <see cref="ResourceFile.Path"/>
/// and lets its <see cref="ResourceFile.Load"/> override populate it. Custom
/// resource types may do the same, or register their loader explicitly with
/// <see cref="RegisterLoader{T}"/>. Every cached resource is shared by path and
/// reference-counted (<see cref="Retain{T}"/> / <see cref="Release{T}"/>); a
/// discarded entry is disposed so it frees what it owns (a model releases its
/// retained textures).
/// </summary>
public sealed class ResourceLibrary
{
    private readonly Dictionary<Type, IResourceCache> _caches = [];
    private readonly Dictionary<Type, Assembly> _owners = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Registers the resource types of <paramref name="assembly"/>: every
    /// <see cref="AssetTypeAttribute"/>-marked type gets its own cache,
    /// populated through <see cref="ResourceFile.Load"/>. Idempotent — an
    /// assembly already registered is skipped. Called by the composition root
    /// for the engine, editor and game assemblies.
    /// </summary>
    public void Register(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsDefined(typeof(AssetTypeAttribute)))
                continue;

            RegisterCacheFor(type);
        }
    }

    /// <summary>
    /// Drops every resource type registered by <paramref name="assembly"/> and
    /// discards their cached entries (each is disposed). Called when a game
    /// assembly is unloaded on a full hot reload, so stale types of the old
    /// generation no longer resolve.
    /// </summary>
    public void Unregister(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        IResourceCache[] caches;
        lock (_lock)
        {
            caches = _owners
                .Where(entry => entry.Value == assembly)
                .Select(entry => _caches[entry.Key])
                .ToArray();

            foreach (var type in _owners.Where(entry => entry.Value == assembly).Select(entry => entry.Key).ToArray())
            {
                _caches.Remove(type);
                _owners.Remove(type);
            }
        }

        foreach (var cache in caches)
            cache.Clear();
    }

    /// <summary>
    /// Registers the loader used to load a resource type by path. Custom
    /// <see cref="ResourceFile"/> subclasses call this to become cacheable
    /// without carrying the <see cref="AssetTypeAttribute"/> marker.
    /// </summary>
    public void RegisterLoader<T>(Func<string, T> loader) where T : ResourceFile
    {
        ArgumentNullException.ThrowIfNull(loader);

        lock (_lock)
        {
            _caches[typeof(T)] = new ResourceCache(path => loader(path));
            _owners[typeof(T)] = typeof(T).Assembly;
        }
    }

    /// <summary>
    /// Registers the cache of one <see cref="AssetTypeAttribute"/>-marked type:
    /// the library allocates each instance through its parameterless
    /// constructor, assigns <see cref="ResourceFile.Path"/> and lets the type's
    /// <see cref="ResourceFile.Load"/> override populate it. The constructor
    /// delegate is compiled once per type, so per-load allocation stays cheap.
    /// </summary>
    private void RegisterCacheFor(Type type)
    {
        var constructor = type.GetConstructor(
                              BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                              binder: null,
                              types: Type.EmptyTypes,
                              modifiers: null)
                          ?? throw new InvalidOperationException(
                              $"File asset '{type.Name}' must expose a parameterless constructor for the library to allocate it.");
        var factory = Expression.Lambda<Func<ResourceFile>>(Expression.New(constructor)).Compile();

        lock (_lock)
        {
            if (_owners.ContainsKey(type))
                return; // already registered (same assembly twice)

            _caches[type] = new ResourceCache(path =>
            {
                var resource = factory();
                resource.Path = path;
                resource.Load();
                return resource;
            });
            _owners[type] = type.Assembly;
        }
    }

    /// <summary>
    /// Loads a resource of the given <paramref name="resourceType"/> by path
    /// (cached, shared by path). Throws on failure.
    /// </summary>
    public ResourceFile Load(Type resourceType, string path)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return GetCache(resourceType).Load(path);
    }

    /// <summary>Loads a resource of type <typeparamref name="T"/> by path (cached, shared by path). Throws on failure.</summary>
    public T Load<T>(string path) where T : ResourceFile => (T)Load(typeof(T), path);

    /// <summary>Asynchronously loads a resource of type <typeparamref name="T"/> (shared cache, in-flight dedup).</summary>
    public async Task<T> LoadAsync<T>(string path, Func<CancellationToken, T> load, CancellationToken cancellationToken = default)
        where T : ResourceFile
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(load);
        return (T)await GetCache(typeof(T))
            .LoadAsync(path, token => load(token), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Returns the cached resource at <paramref name="path"/> without loading it.</summary>
    public bool TryGet<T>(string path, out T? resource) where T : ResourceFile
    {
        if (GetCache(typeof(T)).TryGet(path, out var cached) && cached is T typed)
        {
            resource = typed;
            return true;
        }

        resource = null;
        return false;
    }

    /// <summary>Every cached resource of type <typeparamref name="T"/>.</summary>
    public IReadOnlyList<T> GetAll<T>() where T : ResourceFile =>
        GetCache(typeof(T)).GetAll().OfType<T>().ToArray();

    /// <summary>Records a holder reference for the cached entry at <paramref name="path"/>.</summary>
    public void Retain<T>(string path) where T : ResourceFile => GetCache(typeof(T)).Retain(path);

    /// <summary>Drops a holder reference; the entry is discarded when the last holder releases.</summary>
    public void Release<T>(string path) where T : ResourceFile => GetCache(typeof(T)).Release(path);

    /// <summary>Discards the cached entry at <paramref name="path"/> so the next load re-reads it.</summary>
    public void Invalidate<T>(string path) where T : ResourceFile => GetCache(typeof(T)).Invalidate(path);

    /// <summary>Discards every cached entry of type <typeparamref name="T"/>.</summary>
    public void Clear<T>() where T : ResourceFile => GetCache(typeof(T)).Clear();

    /// <summary>Number of holders recorded for the entry at <paramref name="path"/>.</summary>
    public int GetReferenceCount<T>(string path) where T : ResourceFile => GetCache(typeof(T)).GetReferenceCount(path);

    /// <summary>Number of cached entries of type <typeparamref name="T"/>.</summary>
    public int CachedCount<T>() where T : ResourceFile => GetCache(typeof(T)).Count;

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

    /// <summary>
    /// The cache registered for <paramref name="resourceType"/>, or an
    /// exception when no loader was registered for it.
    /// </summary>
    private IResourceCache GetCache(Type resourceType)
    {
        lock (_lock)
        {
            if (_caches.TryGetValue(resourceType, out var cache))
                return cache;

            throw new InvalidOperationException(
                $"No loader registered for resource type '{resourceType.Name}'. " +
                "Register one with RegisterLoader before loading it.");
        }
    }
}
