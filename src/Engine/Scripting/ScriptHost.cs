using System.Diagnostics;
using System.Reflection;
using Crowbar.FileSystems;

namespace Crowbar.Engine.Scripting;

/// <summary>How a hot reload was applied.</summary>
public enum ScriptReloadMode
{
    /// <summary>Initial load — no previous assembly existed.</summary>
    Initial,

    /// <summary>
    /// Only method bodies changed: the live assembly was patched in place, so
    /// existing instances kept their identity and no state was migrated.
    /// </summary>
    FastPath,

    /// <summary>
    /// Structural change: the assembly was swapped, statics migrated and
    /// watched instance graphs upgraded.
    /// </summary>
    FullReload
}

/// <summary>Raised after a successful hot reload; <see cref="Current"/> is the live assembly.</summary>
public sealed class ScriptReloadedEventArgs : EventArgs
{
    public ScriptReloadedEventArgs(ScriptAssembly? previous, ScriptAssembly current, ScriptReloadMode mode,
        int patchedMethods, int upgradedInstances, TimeSpan duration)
    {
        Previous = previous;
        Current = current;
        Mode = mode;
        PatchedMethods = patchedMethods;
        UpgradedInstances = upgradedInstances;
        Duration = duration;
    }

    /// <summary>The assembly that was live before the reload (null on the initial load).</summary>
    public ScriptAssembly? Previous { get; }

    /// <summary>The live assembly after the reload (unchanged on the fast path).</summary>
    public ScriptAssembly Current { get; }

    /// <summary>How the reload was applied.</summary>
    public ScriptReloadMode Mode { get; }

    /// <summary>Number of method bodies patched in place (fast path only).</summary>
    public int PatchedMethods { get; }

    /// <summary>Number of old-assembly instances upgraded (full reload only).</summary>
    public int UpgradedInstances { get; }

    /// <summary>Time taken by the reload.</summary>
    public TimeSpan Duration { get; }
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
    private readonly List<ScriptAssembly> _codeTargets = [];
    private readonly Lock _gate = new();

    private IFileWatcher? _watcher;
    private FilePath? _directory;
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
        var dir = FileSystem.Project.ToFilePath(directory);
        _directory = dir;

        DisposeGenerations();
        Current = _compiler.CompileDirectory(dir, _assemblyName);

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

        var stopwatch = Stopwatch.StartNew();

        // Snapshot the previous generation's syntax trees BEFORE compiling, so
        // the change can be classified (body-only → IL fast path).
        _compiler.TryGetLastSyntaxTrees(_assemblyName, out var lastTrees);

        ScriptAssembly next;
        try
        {
            next = _compiler.CompileDirectory(_directory.Value, _assemblyName);
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
            LogReloadFailure(ex, previous);
            ReloadFailed?.Invoke(new ScriptReloadFailedEventArgs(ex, previous));
            return false;
        }

        _compiler.TryGetLastSyntaxTrees(_assemblyName, out var newTrees);
        var changedFieldInitializers = lastTrees is not null && newTrees is not null
            ? ScriptChangeClassifier.ChangedFieldInitializers(lastTrees, newTrees)
            : new HashSet<string>(StringComparer.Ordinal);

        // IL fast path: body-only changes patch the live assembly in place, so
        // existing instances keep their identity and run the new code. Falls
        // back to a full reload when the change is structural or unpatchable.
        if (lastTrees is not null && newTrees is not null &&
            ScriptChangeClassifier.Classify(lastTrees, newTrees) is { } changedMethods)
        {
            if (changedMethods.Count == 0)
            {
                // Identical content (same write, no-op reload) — nothing to do.
                next.Dispose();
                return true;
            }

            if (PatchMethods(previous, next, changedMethods) is { } patched)
            {
                lock (_gate)
                    _codeTargets.Add(next); // the patched methods jump into this assembly
                Console.WriteLine($"[Scripting] Hot reload OK ({ScriptReloadMode.FastPath}): {patched} method body(ies) patched in {stopwatch.ElapsedMilliseconds} ms");
                Reloaded?.Invoke(new ScriptReloadedEventArgs(previous, next, ScriptReloadMode.FastPath, patched, 0, stopwatch.Elapsed));
                return true;
            }

            Console.WriteLine("[Scripting] Falling back to a full reload.");
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

            var upgrader = new HotReloadUpgrader(previous.Assembly, next.TypesByFullName, upgraders, changedFieldInitializers);
            upgrader.MigrateStatics(previous.TypesByFullName.Values);
            foreach (var instance in instances)
                upgrader.UpgradeRoot(instance);

            var upgraded = upgrader.UpgradedCount;

            // Best-effort unload of the previous live generation and every
            // assembly its patched methods could jump into (preserved state may
            // legitimately keep old instances alive).
            DisposeGenerations();
            Current = next;

            Console.WriteLine($"[Scripting] Hot reload OK ({ScriptReloadMode.FullReload}): {upgraded} instance(s) upgraded, {previous.TypesByFullName.Count} type(s) reloaded in {stopwatch.ElapsedMilliseconds} ms");
            Reloaded?.Invoke(new ScriptReloadedEventArgs(previous, next, ScriptReloadMode.FullReload, 0, upgraded, stopwatch.Elapsed));
            return true;
        }
        catch (Exception ex)
        {
            next.Dispose();
            LogReloadFailure(ex, previous);
            ReloadFailed?.Invoke(new ScriptReloadFailedEventArgs(ex, previous));
            return false;
        }
    }

    /// <summary>
    /// Patches the changed methods of the live assembly to jump into the
    /// reloaded assembly. Returns the number of patched methods, or null when
    /// any changed method cannot be patched (the caller then does a full reload).
    /// </summary>
    private static int? PatchMethods(ScriptAssembly previous, ScriptAssembly next, IReadOnlySet<string> changedMethods)
    {
        var patched = 0;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var (fullName, oldType) in previous.TypesByFullName)
        {
            if (!next.TypesByFullName.TryGetValue(fullName, out var newType))
                continue;

            IEnumerable<MethodBase> methods = oldType.GetMethods(flags)
                .Cast<MethodBase>()
                .Concat(oldType.GetConstructors(flags));
            foreach (var oldMethod in methods)
            {
                if (!changedMethods.Contains(MethodKey(fullName, oldMethod)))
                    continue;
                if (FindMatchingMethod(newType, oldMethod) is not { } newMethod)
                {
                    Console.WriteLine($"[Scripting] IL fast path unavailable: no match for {fullName}::{oldMethod.Name}");
                    return null;
                }
                if (!MethodBodyPatcher.TryPatch(oldMethod, newMethod, out var error))
                {
                    Console.WriteLine($"[Scripting] IL fast path unavailable for {fullName}::{oldMethod.Name}: {error}");
                    return null;
                }

                patched++;
            }
        }

        return patched > 0 ? patched : null;
    }

    private static MethodBase? FindMatchingMethod(Type newType, MethodBase oldMethod)
    {
        var oldParameters = oldMethod.GetParameters();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var candidate in newType.GetMethods(flags)
                     .Cast<MethodBase>()
                     .Concat(newType.GetConstructors(flags)))
        {
            if (candidate.Name != oldMethod.Name)
                continue;
            var candidateParameters = candidate.GetParameters();
            if (candidateParameters.Length != oldParameters.Length)
                continue;
            var matches = true;
            for (var i = 0; i < oldParameters.Length; i++)
            {
                if (!string.Equals(candidateParameters[i].ParameterType.FullName,
                        oldParameters[i].ParameterType.FullName, StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return candidate;
        }

        return null;
    }

    private static string MethodKey(string typeFullName, MethodBase method)
        => $"{typeFullName}::{method.Name}::{method.GetParameters().Length}";

    private static void LogReloadFailure(Exception error, ScriptAssembly previous)
        => Console.WriteLine($"[Scripting] Hot reload FAILED ({previous.Files.Count} file(s), previous assembly kept): {error.Message}");

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
        DisposeGenerations();
        Current = null;
    }

    /// <summary>Disposes the live assembly and every code target the fast path patched into.</summary>
    private void DisposeGenerations()
    {
        ScriptAssembly[] targets;
        lock (_gate)
        {
            targets = [.. _codeTargets];
            _codeTargets.Clear();
        }

        foreach (var target in targets)
            target.Dispose();
        Current?.Dispose();
    }

    private void StartWatcher()
    {
        if (_directory is not { } directory || !FileSystem.Project.DirectoryExists(directory))
            return;
        try
        {
            _watcher = FileSystem.Project.Watch(directory);
            _watcher.Filter = "*.cs";
            _watcher.IncludeSubdirectories = true;
            _watcher.NotifyFilter = FileChangeFilters.LastWrite | FileChangeFilters.FileName | FileChangeFilters.Size | FileChangeFilters.CreationTime;
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

    private void OnFileSystemEvent(object? sender, FileChangedEventArgs e)
    {
        _reloadRequested = true;
        _reloadNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(200);
    }

    private void RequestReload()
    {
        _reloadRequested = true;
        _reloadNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(200);
    }

    private Dictionary<string, DateTime> TakeSnapshot()
    {
        var snapshot = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        if (_directory is not { } directory)
            return snapshot;
        var fs = FileSystem.Project;
        if (!fs.DirectoryExists(directory))
            return snapshot;
        foreach (var path in fs.EnumerateFiles(directory, "*.cs", recursive: true))
            snapshot[path.FullName] = fs.GetLastWriteTimeUtc(path);
        return snapshot;
    }

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
