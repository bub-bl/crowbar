using System.ComponentModel;
using System.Diagnostics;

namespace Crowbar.UI;

public sealed partial class UiSystem
{
    private string? _razorPath;
    private string? _stylePath;
    private string? _watchDirectory;
    private Dictionary<string, DateTime>? _watchSnapshot;
    private FileSystemWatcher? _watcher;
    private string _razorClassName = "Root";
    private volatile bool _reloadRequested;
    private DateTime _reloadNotBeforeUtc;
    private DateTime _lastRazorWriteUtc;
    private DateTime _lastStyleWriteUtc;
    private DateTime _lastPollUtc = DateTime.MinValue;
    private Task? _scssCompileTask;

    private bool _styleIsScoped;

    public void WatchFiles(string razorPath, string? stylePath = null, string className = "Root")
    {
        StopWatching();
        _razorPath = Path.GetFullPath(razorPath);
        _razorClassName = className;
        if (stylePath is not null)
        {
            _stylePath = Path.GetFullPath(stylePath);
            _styleIsScoped = false;
        }
        else
        {
            var associatedCss = GetAssociatedCssPath(_razorPath);
            if (File.Exists(associatedCss))
            {
                _stylePath = associatedCss;
                _styleIsScoped = true;
            }
            else
            {
                _stylePath = null;
                _styleIsScoped = false;
            }
        }
        _lastRazorWriteUtc = GetWriteTime(_razorPath);
        _lastStyleWriteUtc = _stylePath is null ? DateTime.MinValue : GetWriteTime(_stylePath);
        var directory = Path.GetDirectoryName(_razorPath);
        if (directory is not null) StartWatcher(directory, Path.GetFileName(_razorPath), includeSubdirectories: false);
    }

    /// <summary>Watches every .razor / .razor.css / .razor.scss file under <paramref name="directory"/>.
    /// On change the components are re-registered and the current page is reloaded.
    /// SCSS sources are compiled to their adjacent CSS files before the reload, so
    /// edits to a .razor.scss file are visible without rebuilding the project.</summary>
    public void WatchDirectory(string directory)
    {
        StopWatching();
        _watchDirectory = Path.GetFullPath(directory);
        _watchSnapshot = TakeDirectorySnapshot(_watchDirectory);
        StartWatcher(_watchDirectory, "*.razor*", includeSubdirectories: true);
    }

    /// <summary>
    /// Installs a <see cref="FileSystemWatcher"/> as the primary change source.
    /// Events are debounced in <see cref="ProcessFileReload"/>; a throttled
    /// directory snapshot (every <see cref="PollInterval"/>) remains as a safety
    /// net for changes the watcher misses (network drives, editors that replace
    /// files without events, ...).
    /// </summary>
    private void StartWatcher(string directory, string filter, bool includeSubdirectories)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            _watcher = new FileSystemWatcher(directory, filter)
            {
                IncludeSubdirectories = includeSubdirectories,
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
            // The polling fallback keeps hot reload working if the watcher
            // cannot be created (permissions, missing FS support).
            Console.WriteLine($"[UI] File watcher unavailable: {ex.Message}");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        _reloadRequested = true;
        _reloadNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(200);
    }

    /// <summary>How often the snapshot safety-net poll runs when a watcher is active (2 s).</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public void Update(float deltaTime = 1f / 60f)
    {
        // The FileSystemWatcher is the primary change source; the snapshot poll
        // is only a safety net, so it runs at a fraction of the frame rate.
        if (DateTime.UtcNow - _lastPollUtc >= PollInterval)
        {
            _lastPollUtc = DateTime.UtcNow;
            DetectFileChanges();
        }
        ProcessFileReload();
        RenderRazorIfNeeded();
        AdvanceAnimations(Screen, deltaTime);
        AdvanceCarets(Screen, deltaTime);
    }

    private void ProcessFileReload()
    {
        if (!_reloadRequested || DateTime.UtcNow < _reloadNotBeforeUtc) return;
        try
        {
            if (_watchDirectory is not null)
            {
                // Sass startup is expensive and must never run on the render
                // thread. Compile all entry points in one tool invocation, then
                // apply the resulting CSS on the next UI update.
                if (_scssCompileTask is null)
                {
                    var directory = _watchDirectory;
                    _scssCompileTask = Task.Run(() => CompileScssFiles(directory));
                    return;
                }
                if (!_scssCompileTask.IsCompleted) return;
                _scssCompileTask.GetAwaiter().GetResult();
                _scssCompileTask = null;
                RegisterRazorComponentsFromDirectory(_watchDirectory);
                if (_currentRoute is not null && _pages.Contains(_currentRoute)) Navigate(CurrentUrl);
                else if (_currentRoute is not null) { _currentRoute = null; ShowNotFound(CurrentUrl); }
                else if (_razorRoot is not null) _razorRenderPending = true;
                else if (_pages.Count > 0) Navigate(CurrentUrl);
            }
            else if (_razorPath is not null && File.Exists(_razorPath))
            {
                LoadRazorFromFile(_razorPath, _razorClassName);
            }
            else if (_stylePath is not null && File.Exists(_stylePath))
            {
                if (_styleIsScoped)
                {
                    var scopeId = $"b-{_razorClassName.ToLowerInvariant()}";
                    LoadScopedStyles(_stylePath, ReadStableText(_stylePath), scopeId);
                }
                else
                {
                    LoadStyles(ReadStableText(_stylePath));
                }
            }
            _reloadRequested = false;
            Console.WriteLine("[UI] Hot reload applied.");
        }
        catch (IOException)
        {
            // Atomic saves commonly keep the target locked for a few frames.
            // Keep the request alive and retry after the debounce window.
            _reloadNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(150);
        }
        catch (InvalidOperationException ex)
        {
            // Keep the current valid tree alive when a file is temporarily
            // invalid while the editor is writing it. The next file event will
            // schedule another attempt.
            _reloadRequested = false;
            Console.WriteLine($"[UI] Hot reload skipped: {ex.Message}");
        }
    }

    private void DetectFileChanges()
    {
        if (_watchDirectory is not null)
        {
            var snapshot = TakeDirectorySnapshot(_watchDirectory);
            if (_watchSnapshot is null || !SnapshotEqual(_watchSnapshot, snapshot))
            {
                _watchSnapshot = snapshot;
                RequestReload(_watchDirectory);
            }
            return;
        }
        if (_razorPath is not null)
        {
            var writeTime = GetWriteTime(_razorPath);
            if (writeTime != _lastRazorWriteUtc)
            {
                _lastRazorWriteUtc = writeTime;
                RequestReload(_razorPath);
            }
        }
        if (_stylePath is not null)
        {
            var writeTime = GetWriteTime(_stylePath);
            if (writeTime != _lastStyleWriteUtc)
            {
                _lastStyleWriteUtc = writeTime;
                RequestReload(_stylePath);
            }
        }
    }

    private void RequestReload(string path)
    {
        _reloadRequested = true;
        _reloadNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(200);
        Console.WriteLine($"[UI] Change detected: {Path.GetFileName(path)}");
    }

    private static DateTime GetWriteTime(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    /// <summary>
    /// Rebuilds the non-partial SCSS entry points before the directory reload.
    /// DartSassBuilder is already a project dependency; its command-line tool is
    /// used here so runtime compilation has exactly the same Sass semantics as a
    /// normal build (including @use imports and nesting).
    /// </summary>
    private static void CompileScssFiles(string directory)
    {
        var scssPaths = Directory.EnumerateFiles(directory, "*.scss", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith('_', StringComparison.Ordinal))
            .ToArray();
        if (scssPaths.Length == 0) return;
        if (!TryCompileScss(scssPaths))
            Console.WriteLine($"[UI] SCSS hot reload skipped: compiler unavailable for {scssPaths.Length} file(s).");
    }

    private static bool TryCompileScss(IReadOnlyList<string> scssPaths)
    {
        var compiler = FindSassCompiler();
        if (compiler is null) return false;

        if (!compiler.IsDartSassBuilder && scssPaths.Count > 1)
        {
            // The standalone Sass CLI only accepts one input/output pair; keep
            // the fallback correct while the bundled builder remains batched.
            foreach (var scssPath in scssPaths)
                if (!TryCompileScss([scssPath])) return false;
            return true;
        }

        var outputPaths = scssPaths.Select(path => Path.ChangeExtension(path, ".css")).ToArray();
        var startInfo = new ProcessStartInfo
        {
            FileName = compiler.FileName,
            WorkingDirectory = Path.GetDirectoryName(scssPaths[0]) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in compiler.PrefixArguments)
            startInfo.ArgumentList.Add(argument);
        if (compiler.IsDartSassBuilder)
        {
            startInfo.ArgumentList.Add("files");
            foreach (var scssPath in scssPaths)
                startInfo.ArgumentList.Add(scssPath);
            startInfo.ArgumentList.Add("--outputstyle");
            startInfo.ArgumentList.Add("expanded");
        }
        else
        {
            // The standalone Sass CLI accepts one input/output pair. The
            // bundled DartSassBuilder path above is preferred because it can
            // compile every entry point in one process.
            foreach (var pair in scssPaths.Zip(outputPaths))
            {
                startInfo.ArgumentList.Add(pair.First);
                startInfo.ArgumentList.Add(pair.Second);
            }
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit(10_000);
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                Console.WriteLine($"[UI] SCSS compiler timed out for {Path.GetFileName(scssPaths[0])}.");
                return false;
            }

            if (process.ExitCode != 0)
            {
                _ = outputTask.GetAwaiter().GetResult();
                var error = errorTask.GetAwaiter().GetResult().Trim();
                Console.WriteLine($"[UI] SCSS compile failed for {Path.GetFileName(scssPaths[0])}: {error}");
                return false;
            }
            _ = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            return outputPaths.All(File.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Console.WriteLine($"[UI] SCSS compiler failed for {Path.GetFileName(scssPaths[0])}: {ex.Message}");
            return false;
        }
    }

    private static SassCompilerCommand? FindSassCompiler()
    {
        var packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var toolDirectory = Path.Combine(packageRoot, "dartsassbuilder", "1.1.0", "tool");
        var executable = OperatingSystem.IsWindows() ? "DartSassBuilder.exe" : "DartSassBuilder";
        var toolPath = Path.Combine(toolDirectory, executable);
        if (File.Exists(toolPath)) return new SassCompilerCommand(toolPath, [], true);

        var toolDll = Path.Combine(toolDirectory, "DartSassBuilder.dll");
        if (File.Exists(toolDll)) return new SassCompilerCommand("dotnet", [toolDll], true);

        // A globally installed Dart Sass CLI remains a useful fallback for
        // published builds where the NuGet package cache is not available.
        var sass = OperatingSystem.IsWindows() ? "sass.cmd" : "sass";
        return CanStartCompiler(sass) ? new SassCompilerCommand(sass, [], false) : null;
    }

    private static bool CanStartCompiler(string fileName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null) return false;
            process.WaitForExit(2_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    private sealed record SassCompilerCommand(string FileName, IReadOnlyList<string> PrefixArguments, bool IsDartSassBuilder);

    private static void AdvanceCarets(Panel panel, float deltaTime)
    {
        if (panel is TextInput input) input.AdvanceCaret(deltaTime);
        foreach (var child in panel.Children) AdvanceCarets(child, deltaTime);
    }

    private static bool AdvanceAnimations(Panel panel, float deltaTime)
    {
        var animated = panel.AdvanceStyleAnimation(deltaTime);
        foreach (var child in panel.Children) animated |= AdvanceAnimations(child, deltaTime);
        return animated;
    }

    private static string ReadStableText(string path)
    {
        string? previous = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                if (previous is not null && previous == text) return text;
                previous = text;
                Thread.Sleep(30);
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(25);
            }
        }
        return previous ?? File.ReadAllText(path);
    }

    public void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _razorPath = null;
        _stylePath = null;
        _watchDirectory = null;
        _watchSnapshot = null;
    }

    private static Dictionary<string, DateTime> TakeDirectorySnapshot(string directory)
    {
        var snapshot = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return snapshot;
        foreach (var path in Directory.EnumerateFiles(directory, "*.razor*", SearchOption.AllDirectories))
            snapshot[Path.GetFullPath(path)] = GetWriteTime(path);
        return snapshot;
    }

    private static bool SnapshotEqual(Dictionary<string, DateTime> a, Dictionary<string, DateTime> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, writeTime) in a)
            if (!b.TryGetValue(key, out var other) || other != writeTime) return false;
        return true;
    }
}
