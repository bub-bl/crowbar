using Crowbar.FileSystems;

namespace Crowbar.Engine;

/// <summary>
/// A process-wide cache of file-backed resources keyed by their canonical
/// content path, so loading the same path twice returns the same instance and
/// a model or texture is never imported or decoded more than once per path.
///
/// Reference counting is holder-based: <see cref="Load"/> returns the shared
/// instance without recording a holder (the cache itself keeps the entry
/// alive), <see cref="Retain"/> records a holder and <see cref="Release"/>
/// drops one. When the last holder releases, the entry is discarded and the
/// optional <c>released</c> callback runs so the resource can free what it owns.
/// <see cref="Invalidate"/> discards an entry immediately (asset reload) and
/// <see cref="Clear"/> discards everything.
/// </summary>
internal sealed class ResourceCache<T> where T : class
{
    private sealed class Entry
    {
        public required T Value { get; init; }
        public int References;
    }

    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Dictionary<string, Task<T>> _inFlight = [];
    private readonly Func<string, T> _load;
    private readonly Action<T>? _released;

    public ResourceCache(Func<string, T> load, Action<T>? released = null)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _released = released;
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

    /// <summary>Returns the cached instance for <paramref name="path"/>, or false when not loaded.</summary>
    public bool TryGet(string path, out T value)
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

        value = null!;
        return false;
    }

    /// <summary>Snapshot of every cached value (for enumeration, e.g. <c>ResourceLibrary.GetAll</c>).</summary>
    public T[] Snapshot()
    {
        lock (_entries)
            return [.. _entries.Values.Select(entry => entry.Value)];
    }

    /// <summary>
    /// Returns the shared instance for <paramref name="path"/>, loading it on
    /// first use. Does not record a holder; call <see cref="Retain"/> when the
    /// caller keeps a long-lived reference it wants to keep the entry alive for.
    /// </summary>
    public T Load(string path)
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
    public Task<T> LoadAsync(string path, Func<CancellationToken, T> load, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(load);

        var key = CanonicalKey(path);
        Task<T> task;
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
                        // and release the duplicate we just produced.
                        _released?.Invoke(value);
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
    /// and its <c>released</c> callback runs. Entries with no holders are left
    /// in place (the cache owns them) until invalidated or cleared.
    /// </summary>
    public void Release(string path)
    {
        var key = CanonicalKey(path);
        T? released = null;
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
            _released?.Invoke(released);
    }

    /// <summary>Discards the entry at <paramref name="path"/> so the next load re-imports it.</summary>
    public void Invalidate(string path)
    {
        var key = CanonicalKey(path);
        T? released = null;
        lock (_entries)
        {
            if (_entries.Remove(key, out var entry))
                released = entry.Value;
        }

        if (released is not null)
            _released?.Invoke(released);
    }

    /// <summary>Discards every entry and runs each entry's <c>released</c> callback.</summary>
    public void Clear()
    {
        T[] released;
        lock (_entries)
        {
            released = [.. _entries.Values.Select(entry => entry.Value)];
            _entries.Clear();
        }

        foreach (var value in released)
            _released?.Invoke(value);
    }

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
