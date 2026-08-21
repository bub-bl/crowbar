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
/// and the classes' <c>Load</c> statics delegate to it. Loading is exposed
/// through each type's static (<c>Model.Load</c>, <c>Shader.Load</c>, ...); the
/// library's own <see cref="Load"/> / <see cref="LoadAsync{T}"/> are internal
/// implementation those facades call. Registering an assembly also builds an
/// extension → type registry from the <see cref="AssetTypeAttribute"/>
/// declarations (<see cref="GetTypeForExtension"/>), so a file extension
/// resolves to the resource type that loads it. Content code never repeats the
/// try/catch dance — <see cref="LoadModel"/> substitutes
/// <see cref="Model.Error"/> for an unreadable model.
///
/// Which assemblies participate is decided by the composition root, not by
/// this library: each assembly's resource types are registered with
/// <see cref="Register"/>. The application host registers the engine's own
/// types (Model, Texture2D, AudioClip, Shader) at startup, the editor registers
/// its assembly, and the game project registers its loaded assembly.
/// A type participates by marking itself <see cref="AssetTypeAttribute"/>, which
/// is mandatory for every <see cref="ResourceFile"/> subclass; the library
/// allocates each instance, assigns its <see cref="ResourceFile.Path"/>
/// and lets its <see cref="ResourceFile.Load"/> override populate it. Custom
/// resource types may do the same, or override the load itself with
/// <see cref="RegisterLoader{T}"/>. Every cached resource is shared by path and
/// reference-counted (<see cref="Retain{T}"/> / <see cref="Release{T}"/>); a
/// discarded entry is disposed so it frees what it owns (a model releases its
/// retained textures).
/// </summary>
public sealed class ResourceLibrary
{
    private readonly Dictionary<Type, IResourceCache> _caches = [];
    private readonly Dictionary<Type, Assembly> _owners = [];
    private readonly Dictionary<string, Type> _extensions = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Registers the resource types of <paramref name="assembly"/>: every
    /// <see cref="AssetTypeAttribute"/>-marked type gets its own cache,
    /// populated through <see cref="ResourceFile.Load"/>. The marker is
    /// mandatory — an assembly containing a concrete <see cref="ResourceFile"/>
    /// subclass without it is rejected — and registration is idempotent: an
    /// assembly already registered is skipped. Called by the composition root
    /// for the engine, editor and game assemblies.
    /// </summary>
    public void Register(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var types = assembly.GetTypes();

        // Every concrete ResourceFile subclass must declare its file type;
        // without the marker it would silently never be loadable through the
        // library. Fail fast here so a new type missing the attribute cannot
        // slip in unnoticed.
        var unmarked = types
            .Where(type => !type.IsAbstract
                           && !type.IsGenericTypeDefinition
                           && typeof(ResourceFile).IsAssignableFrom(type)
                           && !type.IsDefined(typeof(AssetTypeAttribute)))
            .ToArray();
        if (unmarked.Length > 0)
        {
            throw new InvalidOperationException(
                "Every ResourceFile subclass must be marked with [AssetType] to declare its file type. " +
                $"Missing on: {string.Join(", ", unmarked.Select(type => type.Name))}.");
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition || !type.IsDefined(typeof(AssetTypeAttribute)))
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

            // The extension registry must not keep pointing at the unloaded
            // generation's types: drop every extension whose type belongs to
            // the assembly being unregistered, or GetTypeForExtension would
            // resolve to a stale Type from a dead assembly.
            foreach (var extension in _extensions
                         .Where(entry => entry.Value.Assembly == assembly)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _extensions.Remove(extension);
            }
        }

        foreach (var cache in caches)
            cache.Clear();
    }

    /// <summary>
    /// Registers a custom loader used to load a resource type by path. The
    /// <see cref="AssetTypeAttribute"/> marker stays mandatory — it declares
    /// the type as file-backed — and this only overrides <em>how</em> the type
    /// is loaded (construction and <see cref="ResourceFile.Load"/> are replaced
    /// by the given loader).
    /// </summary>
    public void RegisterLoader<T>(Func<string, T> loader) where T : ResourceFile
    {
        ArgumentNullException.ThrowIfNull(loader);
        if (!typeof(T).IsDefined(typeof(AssetTypeAttribute)))
        {
            throw new InvalidOperationException(
                $"Resource type '{typeof(T).Name}' must be marked with [AssetType] to declare its file type " +
                "before a loader can be registered.");
        }

        lock (_lock)
        {
            _caches[typeof(T)] = new ResourceCache(path => loader(path));
            _owners[typeof(T)] = typeof(T).Assembly;
            foreach (var extension in typeof(T).GetCustomAttribute<AssetTypeAttribute>()?.Extensions ?? [])
                AddExtension(extension, typeof(T));
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
            foreach (var extension in type.GetCustomAttribute<AssetTypeAttribute>()?.Extensions ?? [])
                AddExtension(extension, type);
        }
    }

    /// <summary>Records that <paramref name="type"/> loads <paramref name="extension"/>, rejecting collisions.</summary>
    private void AddExtension(string extension, Type type)
    {
        var key = NormalizeExtension(extension);
        if (key.Length == 0)
            return;

        lock (_lock)
        {
            if (_extensions.TryGetValue(key, out var existing))
            {
                if (existing != type)
                {
                    throw new InvalidOperationException(
                        $"The file extension '{key}' is already declared by '{existing.Name}'; an extension belongs to one resource type only.");
                }
                return;
            }

            _extensions[key] = type;
        }
    }

    /// <summary>Canonical extension form: trimmed, leading dot stripped, lower-cased.</summary>
    private static string NormalizeExtension(string extension) =>
        extension.Trim().TrimStart('.').ToLowerInvariant();

    /// <summary>
    /// Returns the resource type that declared <paramref name="extension"/> in
    /// its <see cref="AssetTypeAttribute"/>, or null when no registered type
    /// loads it. The match is case-insensitive and the leading dot is optional
    /// ("gltf", ".GLTF" and "Gltf" all resolve to <see cref="Model"/>).
    /// </summary>
    public Type? GetTypeForExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var key = NormalizeExtension(extension);
        lock (_lock)
            return _extensions.TryGetValue(key, out var type) ? type : null;
    }

    /// <summary>Every registered file extension mapped to its resource type (snapshot).</summary>
    public IReadOnlyDictionary<string, Type> Extensions
    {
        get
        {
            lock (_lock)
                return new Dictionary<string, Type>(_extensions);
        }
    }

    /// <summary>
    /// Loads a resource of the given <paramref name="resourceType"/> by path
    /// (cached, shared by path). Throws on failure. Internal: the public
    /// loading surface is each type's static (Model.Load, Shader.Load, ...).
    /// The path is the address: game content is addressed explicitly as
    /// <c>Content/...</c>, engine content as <c>Assets/...</c> or
    /// <c>Shaders/...</c> — no implicit prefix or fallback is applied.
    /// </summary>
    internal ResourceFile Load(Type resourceType, string path)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return GetCache(resourceType).Load(path);
    }

    /// <summary>Internal: loads a resource of type <typeparamref name="T"/> by path (cached, shared by path). Throws on failure.</summary>
    internal T Load<T>(string path) where T : ResourceFile => (T)Load(typeof(T), path);

    /// <summary>
    /// Internal: asynchronously loads a resource of type <typeparamref name="T"/>
    /// (shared cache, in-flight dedup). The path is the address (game content
    /// as <c>Content/...</c>, engine content as <c>Assets/...</c> /
    /// <c>Shaders/...</c>) and the loader receives it as-is, so it reads the
    /// file the entry is cached under.
    /// </summary>
    internal async Task<T> LoadAsync<T>(string path, Func<string, CancellationToken, T> load, CancellationToken cancellationToken = default)
        where T : ResourceFile
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(load);
        return (T)await GetCache(typeof(T))
            .LoadAsync(path, (_, token) => load(path, token), cancellationToken)
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

    /// <summary>
    /// Discards the cached entry at <paramref name="path"/> so the next load
    /// re-reads it. The resource type is resolved from the path's extension
    /// through the extension registry — the content hot-reload path: the
    /// host's content watcher calls this with a changed file, whatever its
    /// type. Returns false when no registered resource type loads the
    /// extension (the file is not a known asset, so nothing is invalidated).
    /// </summary>
    public bool Invalidate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var type = GetTypeForExtension(Path.GetExtension(path));
        if (type is null)
            return false;
        GetCache(type).Invalidate(path);
        return true;
    }

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
