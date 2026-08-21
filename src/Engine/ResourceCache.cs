using Crowbar.FileSystems;

namespace Crowbar.Engine;

/// <summary>
/// Non-generic view of a per-type resource cache, so the library can hold
/// every type's cache in one <see cref="Dictionary{Type, IResourceCache}"/>
/// and retrieve a resource by its <see cref="Type"/>.
/// </summary>
internal interface IResourceCache
{
    /// <summary>Number of cached entries (regardless of how many holders each has).</summary>
    int Count { get; }

    /// <summary>Returns the cached resource for <paramref name="path"/>, loading it on first use.</summary>
    ResourceFile Load(string path);

    /// <summary>Returns the cached resource for <paramref name="path"/>, or false when not loaded.</summary>
    bool TryGet(string path, out ResourceFile? resource);

    /// <summary>Returns the shared resource, loading it off the calling thread when not cached yet.</summary>
    Task<ResourceFile> LoadAsync(string path, Func<CancellationToken, ResourceFile> load, CancellationToken cancellationToken);

    /// <summary>Every cached resource of this type.</summary>
    IReadOnlyList<ResourceFile> GetAll();

    /// <summary>Records a holder reference for the cached entry at <paramref name="path"/>.</summary>
    void Retain(string path);

    /// <summary>Drops a holder reference; the entry is discarded when the last holder releases.</summary>
    void Release(string path);

    /// <summary>Discards the entry at <paramref name="path"/> so the next load re-imports it.</summary>
    void Invalidate(string path);

    /// <summary>Discards every cached entry.</summary>
    void Clear();

    /// <summary>Number of holders recorded for the entry at <paramref name="path"/>.</summary>
    int GetReferenceCount(string path);
}

/// <summary>
/// A process-wide cache of file-backed resources keyed by their canonical
/// content path, so loading the same path twice returns the same instance and
/// a model or texture is never imported or decoded more than once per path.
///
/// Reference counting is holder-based: <see cref="Load"/> returns the shared
/// instance without recording a holder (the cache itself keeps the entry
/// alive), <see cref="Retain"/> records a holder and <see cref="Release"/>
/// drops one. When the last holder releases, the entry is discarded and
/// disposed (resources are <see cref="IDisposable"/> through
/// <see cref="ResourceFile"/>, so they free what they own — a model releases
/// its retained textures). <see cref="Invalidate"/> discards an entry
/// immediately (asset reload) and <see cref="Clear"/> discards everything.
/// </summary>
internal sealed class ResourceCache : IResourceCache
{
    private sealed class Entry
    {
        public required ResourceFile Value { get; init; }
        public int References;
    }

    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Dictionary<string, Task<ResourceFile>> _inFlight = [];
    private readonly Func<string, ResourceFile> _load;

    public ResourceCache(Func<string, ResourceFile> load)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
    }

    /// <summary>Number of cached entries (regardless of how many holders each has).</summary>
    public int Count
    {
        get
        {
            lock (_entries)
                return _entries.Count;
        }
    }

    /// <summary>Returns the cached resource for <paramref name="path"/>, or false when not loaded.</summary>
    public bool TryGet(string path, out ResourceFile? value)
    {
        var key = CanonicalKey(path);
        lock (_entries)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>Every cached resource (for enumeration, e.g. <c>ResourceLibrary.GetAll</c>).</summary>
    public IReadOnlyList<ResourceFile> GetAll()
    {
        lock (_entries)
            return [.. _entries.Values.Select(entry => entry.Value)];
    }

    /// <summary>
    /// Returns the shared instance for <paramref name="path"/>, loading it on
    /// first use. Does not record a holder; call <see cref="Retain"/> when the
    /// caller keeps a long-lived reference it wants to keep the entry alive for.
    /// </summary>
    public ResourceFile Load(string path)
    {
        var key = CanonicalKey(path);
        lock (_entries)
        {
            if (_entries.TryGetValue(key, out var entry))
                return entry.Value;

            var value = _load(path);
            _entries.Add(key, new Entry { Value = value });
            return value;
        }
    }

    /// <summary>
    /// Returns the shared instance for <paramref name="path"/>, running the
    /// given loader off the calling thread when the entry is not cached yet.
    /// Concurrent loads of the same path share one in-flight task, and the
    /// completed value is installed into the cache so later (sync or async)
    /// loads reuse it. A pre-cancelled token skips the load entirely.
    /// </summary>
    public Task<ResourceFile> LoadAsync(
        string path,
        Func<CancellationToken, ResourceFile> load,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(load);

        var key = CanonicalKey(path);
        Task<ResourceFile> task;
        lock (_entries)
        {
            if (_entries.TryGetValue(key, out var entry))
                return Task.FromResult(entry.Value);
            if (_inFlight.TryGetValue(key, out var inFlight))
                return inFlight;

            task = Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = load(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                lock (_entries)
                {
                    if (_entries.TryGetValue(key, out var existing))
                    {
                        // Another load finished first: keep the shared instance
                        // and dispose the duplicate we just produced.
                        Dispose(value);
                        return existing.Value;
                    }

                    _entries.Add(key, new Entry { Value = value });
                    return value;
                }
            }, cancellationToken);

            _inFlight.Add(key, task);
        }

        _ = task.ContinueWith(
            _ =>
            {
                lock (_entries)
                    _inFlight.Remove(key);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    /// <summary>Records an additional holder for the entry at <paramref name="path"/>.</summary>
    public void Retain(string path)
    {
        var key = CanonicalKey(path);
        lock (_entries)
        {
            if (_entries.TryGetValue(key, out var entry))
                entry.References++;
        }
    }

    /// <summary>
    /// Drops one holder. When the last holder releases, the entry is discarded
    /// and disposed. Entries with no holders are left in place (the cache owns
    /// them) until invalidated or cleared.
    /// </summary>
    public void Release(string path)
    {
        var key = CanonicalKey(path);
        ResourceFile? released = null;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.References == 0)
                return;

            entry.References--;
            if (entry.References > 0)
                return;

            _entries.Remove(key);
            released = entry.Value;
        }

        if (released is not null)
            Dispose(released);
    }

    /// <summary>Discards the entry at <paramref name="path"/> so the next load re-imports it.</summary>
    public void Invalidate(string path)
    {
        var key = CanonicalKey(path);
        ResourceFile? released = null;
        lock (_entries)
        {
            if (_entries.Remove(key, out var entry))
                released = entry.Value;
        }

        if (released is not null)
            Dispose(released);
    }

    /// <summary>Discards every entry and disposes each one.</summary>
    public void Clear()
    {
        ResourceFile[] released;
        lock (_entries)
        {
            released = [.. _entries.Values.Select(entry => entry.Value)];
            _entries.Clear();
        }

        foreach (var value in released)
            Dispose(value);
    }

    /// <summary>Disposes a discarded resource so it frees what it owns (models release their textures).</summary>
    private static void Dispose(ResourceFile value) => value.Dispose();

    /// <summary>Number of holders recorded for the entry at <paramref name="path"/>.</summary>
    public int GetReferenceCount(string path)
    {
        var key = CanonicalKey(path);
        lock (_entries)
            return _entries.TryGetValue(key, out var entry) ? entry.References : 0;
    }

    /// <summary>
    /// The stable identity used as a cache key: the content path resolved
    /// through the configured filesystem, so logical, relative and absolute
    /// spellings of the same file collapse to one key.
    /// </summary>
    internal static string CanonicalKey(string path) => FileSystem.Content.ToFilePath(path).FullName;
}
