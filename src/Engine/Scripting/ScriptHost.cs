namespace Crowbar.Engine.Scripting;

/// <summary>Raised after a successful hot reload; <see cref="Current"/> is the new assembly.</summary>
public sealed class ScriptReloadedEventArgs : EventArgs
{
    public ScriptReloadedEventArgs(ScriptAssembly previous, ScriptAssembly current)
    {
        Previous = previous;
        Current = current;
    }

    /// <summary>The assembly that was in use before the reload (still valid for the duration of the event).</summary>
    public ScriptAssembly Previous { get; }

    /// <summary>The reloaded assembly now in use.</summary>
    public ScriptAssembly Current { get; }
}

/// <summary>Raised when a hot reload failed; the previous assembly stays active.</summary>
public sealed class ScriptReloadFailedEventArgs : EventArgs
{
    public ScriptReloadFailedEventArgs(Exception error, ScriptAssembly? previous)
    {
        Error = error;
        Previous = previous;
    }

    public Exception Error { get; }

    /// <summary>The assembly that remains in use, or null if the initial compile failed.</summary>
    public ScriptAssembly? Previous { get; }
}

/// <summary>
/// Hosts a set of game script files with hot reload, preserving state across
/// swaps (the analog of s&amp;box's Hotload orchestration). Watching a directory
/// compiles it and reloads it whenever a source file changes:
///
/// <list type="bullet">
/// <item>Static field values of the script assembly are migrated into the
/// reloaded assembly (opt out with <see cref="SkipHotloadAttribute"/>).</item>
/// <item>Object graphs rooted at instances registered through
/// <see cref="WatchInstance"/> are upgraded in place: old-assembly objects are
/// replaced by reloaded instances with identity preserved.</item>
/// <item>Custom migration logic can be plugged in through
/// <see cref="RegisterUpgrader"/>; the default reflection copier runs last.</item>
/// </list>
///
/// Call <see cref="Update"/> once per frame while watching so file changes are
/// picked up (debounced, with a polling safety net). <see cref="Reload"/> can
/// also be invoked directly.
/// </summary>
public sealed class ScriptHost : IDisposable
{
    private static readonly IInstanceUpgrader DefaultUpgrader = new DefaultInstanceUpgrader();

    private readonly ScriptCompiler _compiler;
    private readonly List<IInstanceUpgrader> _upgraders = [];
    private readonly List<object> _watchedInstances = [];
    private readonly object _gate = new();

    private FileSystemWatcher? _watcher;
    private string? _directory;
    private string _assemblyName = "GameScripts";
    private Dictionary<string, DateTime>? _snapshot;
    private DateTime _lastPollUtc = DateTime.MinValue;

    private volatile bool _reloadRequested;
    private DateTime _reloadNotBeforeUtc;

    public ScriptHost(ScriptCompiler? compiler = null)
    {
        // By default scripts can reference the engine assembly (and the whole
        // trusted platform set, always included by the compiler).
        _compiler = compiler ?? new ScriptCompiler([typeof(ScriptHost).Assembly]);
    }

    /// <summary>The currently active script assembly, or null before <see cref="WatchDirectory"/>.</summary>
    public ScriptAssembly? Current { get; private set; }

    public event Action<ScriptReloadedEventArgs>? Reloaded;
    public event Action<ScriptReloadFailedEventArgs>? ReloadFailed;

    /// <summary>
    /// Adds a custom instance upgrader. Upgraders are consulted in
    /// registration order; the default reflection copier always runs last.
    /// </summary>
    public void RegisterUpgrader(IInstanceUpgrader upgrader)
    {
        ArgumentNullException.ThrowIfNull(upgrader);
        lock (_gate)
            _upgraders.Add(upgrader);
    }

    /// <summary>
    /// Registers an object root whose reachable object graph is upgraded on
    /// every reload: old-assembly objects are replaced by reloaded instances
    /// in place, so engine-held references to script objects observe the new
    /// types after the swap.
    /// </summary>
    public void WatchInstance(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (_gate)
            _watchedInstances.Add(instance);
    }

    /// <summary>
    /// Compiles every *.cs file under <paramref name="directory"/> and starts
    /// watching it for changes. Returns the initial <see cref="ScriptAssembly"/>.
    /// </summary>
    public ScriptAssembly WatchDirectory(string directory, string assemblyName = "GameScripts")
    {
        _assemblyName = assemblyName;
        _directory = Path.GetFullPath(directory);

        Current?.Dispose();
        Current = _compiler.CompileDirectory(_directory, _assemblyName);

        _snapshot = TakeSnapshot();
        StopWatcher();
        StartWatcher();
        return Current;
    }

    /// <summary>
    /// Recompiles the watched directory and, on success, swaps the active
    /// assembly while migrating preserved state. Returns false (without
    /// touching the current assembly) when the compile or the migration fails.
    /// </summary>
    public bool Reload()
    {
        var previous = Current;
        if (previous is null || _directory is null)
            return false;

        ScriptAssembly next;
        try
        {
            next = _compiler.CompileDirectory(_directory, _assemblyName);
        }
        catch (IOException)
        {
            // The file is mid-write (atomic save) — retry after the debounce.
            _reloadNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(150);
            _reloadRequested = true;
            return false;
        }
        catch (Exception ex)
        {
            ReloadFailed?.Invoke(new ScriptReloadFailedEventArgs(ex, previous));
            return false;
        }

        try
        {
            IInstanceUpgrader[] upgraders;
            object[] instances;
            lock (_gate)
            {
                upgraders = [.. _upgraders, DefaultUpgrader];
                instances = _watchedInstances.ToArray();
            }

            var upgrader = new HotReloadUpgrader(previous.Assembly, next.TypesByFullName, upgraders);
            upgrader.MigrateStatics(previous.TypesByFullName.Values);
            foreach (var instance in instances)
                upgrader.UpgradeRoot(instance);

            Current = next;
        }
        catch (Exception ex)
        {
            next.Dispose();
            ReloadFailed?.Invoke(new ScriptReloadFailedEventArgs(ex, previous));
            return false;
        }

        // Best-effort unload of the previous generation (preserved state may
        // legitimately keep old instances alive).
        previous.Dispose();

        Reloaded?.Invoke(new ScriptReloadedEventArgs(previous, next));
        return true;
    }

    /// <summary>
    /// Picks up pending file changes and applies the reload. Call once per
    /// frame while watching (a 2 s polling snapshot backs up the watcher as a
    /// safety net for editors that replace files without events).
    /// </summary>
    public void Update()
    {
        if (DateTime.UtcNow - _lastPollUtc >= TimeSpan.FromSeconds(2))
        {
            _lastPollUtc = DateTime.UtcNow;
            if (_directory is not null)
            {
                var snapshot = TakeSnapshot();
                if (_snapshot is not null && !SnapshotEqual(_snapshot, snapshot))
                {
                    _snapshot = snapshot;
                    RequestReload();
                }
            }
        }

        if (!_reloadRequested || DateTime.UtcNow < _reloadNotBeforeUtc)
            return;

        _reloadRequested = false;
        Reload();
    }

    public void Dispose()
    {
        StopWatcher();
        Current?.Dispose();
        Current = null;
    }

    private void StartWatcher()
    {
        if (_directory is null || !Directory.Exists(_directory))
            return;
        try
        {
            _watcher = new FileSystemWatcher(_directory, "*.cs")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime
            };
            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // The polling snapshot keeps hot reload working without the watcher.
            Console.WriteLine($"[Scripting] File watcher unavailable: {ex.Message}");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void StopWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        _reloadRequested = true;
        _reloadNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(200);
    }

    private void RequestReload()
    {
        _reloadRequested = true;
        _reloadNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(200);
    }

    private static Dictionary<string, DateTime> TakeSnapshot(string directory)
    {
        var snapshot = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
            return snapshot;
        foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            snapshot[Path.GetFullPath(path)] = File.GetLastWriteTimeUtc(path);
        return snapshot;
    }

    private Dictionary<string, DateTime> TakeSnapshot() => TakeSnapshot(_directory ?? string.Empty);

    private static bool SnapshotEqual(Dictionary<string, DateTime> a, Dictionary<string, DateTime> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var (key, writeTime) in a)
        {
            if (!b.TryGetValue(key, out var other) || other != writeTime)
                return false;
        }

        return true;
    }
}
