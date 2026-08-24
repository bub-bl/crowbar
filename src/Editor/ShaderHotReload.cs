using System.Diagnostics;
using Crowbar.FileSystems;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// Hot reload for post-process shaders. Watches the engine's
/// <c>Shaders/PostProcesses/</c> source folder (dev tree) and the open
/// project's <c>Content/Shaders/</c> folder, recompiles edited <c>.slang</c>
/// files with the vendored slangc into the folder the runtime loads from, and
/// invalidates the shader + pipeline caches so the next frame picks the change
/// up — no rebuild, no relaunch. Compilation errors surface as notifications.
///
/// Game-shipped shaders (<c>Content/Shaders/</c>) are not part of the engine
/// build, so <see cref="Start"/> also compiles every <c>.slang</c> already
/// present there: that is what materializes their WGSL + reflection sidecar on
/// editor start and on project switch.
/// </summary>
public sealed class ShaderHotReload : IDisposable
{
    private readonly NotificationWindow _notificationWindow;
    private readonly object _lock = new();

    // Source path → last write time, debounced in Update() so a save in
    // progress never compiles a half-written file.
    private readonly Dictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _engineWatcher;
    private FileSystemWatcher? _projectWatcher;
    private ShaderWatch? _engineWatch;
    private ShaderWatch? _projectWatch;
    private string? _slangcPath;
    private string? _commonInclude;
    private bool _disposed;

    /// <summary>One watched shader root and where its compiled artifacts go.</summary>
    private sealed record ShaderWatch(string SourceRoot, string OutputRoot, string LoadPrefix);

    public ShaderHotReload(Editor editor, NotificationWindow notificationWindow)
    {
        _notificationWindow = notificationWindow;
    }

    /// <summary>
    /// Starts (or restarts, on project switch) the watches. First compiles every
    /// <c>.slang</c> already present in the project's <c>Content/Shaders/</c>
    /// (game shaders only materialize their WGSL at runtime), then watches both
    /// shader roots for edits.
    /// </summary>
    public void Start()
    {
        StopWatches();
        ResolveToolchain();

        var projectRoot = FileSystem.Project.ContentRoot;
        if (!string.IsNullOrEmpty(projectRoot))
        {
            var projectShaders = Path.Combine(projectRoot, "Content", "Shaders");
            if (Directory.Exists(projectShaders))
            {
                _projectWatch = new ShaderWatch(projectShaders, projectShaders, "Content/Shaders");
                foreach (var source in Directory.GetFiles(projectShaders, "*.slang", SearchOption.AllDirectories))
                    CompileAndApply(source, _projectWatch);
                _projectWatcher = CreateWatcher(projectShaders);
            }
        }

        if (_engineWatch is not null && Directory.Exists(_engineWatch.SourceRoot))
            _engineWatcher = CreateWatcher(_engineWatch.SourceRoot);
    }

    /// <summary>Applies the debounced recompiles (called once per frame).</summary>
    public void Update()
    {
        List<(string Source, ShaderWatch Watch)> ready = [];
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            foreach (var path in _pending.Keys.ToArray())
            {
                if (!File.Exists(path))
                {
                    _pending.Remove(path); // deleted: nothing to compile, stale artifacts are harmless
                    continue;
                }

                // Wait until the file's write time settles (a save in progress
                // keeps bumping it), then compile once.
                var written = File.GetLastWriteTimeUtc(path);
                if ((now - written).TotalMilliseconds < 200)
                    continue;
                _pending.Remove(path);
                ready.Add((path, WatchFor(path)));
            }
        }

        foreach (var (source, watch) in ready)
            CompileAndApply(source, watch);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopWatches();
    }

    private void StopWatches()
    {
        _engineWatcher?.Dispose();
        _engineWatcher = null;
        _projectWatcher?.Dispose();
        _projectWatcher = null;
    }

    private FileSystemWatcher CreateWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory, "*.slang")
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };
        watcher.Changed += OnShaderChanged;
        watcher.Created += OnShaderChanged;
        return watcher;
    }

    private void OnShaderChanged(object sender, FileSystemEventArgs e)
    {
        lock (_lock)
            _pending[e.FullPath] = DateTime.UtcNow;
    }

    private ShaderWatch WatchFor(string sourcePath)
    {
        if (_engineWatch is not null && IsUnder(sourcePath, _engineWatch.SourceRoot))
            return _engineWatch;
        return _projectWatch!;
    }

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
        (path.Length == root.Length || path[root.Length] is '\\' or '/');

    private void CompileAndApply(string sourcePath, ShaderWatch watch)
    {
        if (!Recompile(sourcePath, watch))
            return;

        var relative = Path.GetRelativePath(watch.SourceRoot, sourcePath);
        var loadPath = $"{watch.LoadPrefix}/{Path.ChangeExtension(relative, ".wgsl")}".Replace('\\', '/');
        Game.Renderer?.InvalidatePostProcessShader(loadPath);
        Log.Info($"[Shaders] Hot reloaded '{loadPath}'");
        UiNotifications.Show("Shaders", $"Reloaded {Path.GetFileName(loadPath)}", "success");
    }

    /// <summary>Runs slangc on <paramref name="sourcePath"/> into the watch's output root. True on success.</summary>
    private bool Recompile(string sourcePath, ShaderWatch watch)
    {
        if (_slangcPath is null || _commonInclude is null)
            return false;

        var name = Path.GetFileNameWithoutExtension(sourcePath);
        Directory.CreateDirectory(watch.OutputRoot);
        var wgslPath = Path.Combine(watch.OutputRoot, name + ".wgsl");
        var jsonPath = Path.Combine(watch.OutputRoot, name + ".slang.json");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _slangcPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add("-target");
            startInfo.ArgumentList.Add("wgsl");
            startInfo.ArgumentList.Add("-I");
            startInfo.ArgumentList.Add(_commonInclude);
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(wgslPath);
            startInfo.ArgumentList.Add("-reflection-json");
            startInfo.ArgumentList.Add(jsonPath);

            using var process = Process.Start(startInfo);
            if (process is null)
                return false;
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var detail = FirstLine(stderr);
                Log.Warn($"[Shaders] Compile failed for '{name}': {detail}");
                UiNotifications.Show("Shaders", $"Compile failed: {name} — {detail}", "error");
                _notificationWindow.Show();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[Shaders] Failed to compile '{name}': {ex.Message}");
            return false;
        }
    }

    private void ResolveToolchain()
    {
        var repoRoot = ResolveRepoRoot();
        if (repoRoot is null)
            return;

        _slangcPath = Path.Combine(repoRoot, "tools", "slang", "slangc.exe");
        _commonInclude = Path.Combine(repoRoot, "Shaders", "Common");
        // Engine shaders compile into the output dir the runtime loads from
        // (the build ships them there); game shaders compile next to their
        // source, which the /Content mount serves.
        _engineWatch = new ShaderWatch(
            Path.Combine(repoRoot, "Shaders", "PostProcesses"),
            Path.Combine(AppContext.BaseDirectory, "Shaders", "PostProcesses"),
            "Shaders/PostProcesses");
    }

    /// <summary>
    /// The repo root in dev (five levels up from the editor's bin folder), or
    /// null in a published build where there is no source tree to watch.
    /// </summary>
    private static string? ResolveRepoRoot()
    {
        var probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Directory.Exists(Path.Combine(probe, "Shaders")) ? probe : null;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "see output" : line.Trim();
    }
}
