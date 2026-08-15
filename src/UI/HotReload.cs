using System.ComponentModel;
using System.Diagnostics;
using Crowbar.Files;
using Zio;

namespace Crowbar.UI;

public sealed partial class UiSystem
{
    private UPath? _razorPath;
    private UPath? _stylePath;
    private UPath? _watchDirectory;
    private Dictionary<string, DateTime>? _watchSnapshot;
    private IFileSystemWatcher? _watcher;
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
        var fs = FileSystemService.Default;
        var razor = fs.ToUPath(razorPath);
        _razorPath = razor;
        _razorClassName = className;
        if (stylePath is not null)
        {
            _stylePath = fs.ToUPath(stylePath);
            _styleIsScoped = false;
        }
        else
        {
            var associatedCss = GetAssociatedCssPath(razor);
            if (fs.FileExists(associatedCss))
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
        _lastRazorWriteUtc = GetWriteTime(razor);
        _lastStyleWriteUtc = _stylePath is { } style ? GetWriteTime(style) : DateTime.MinValue;
        StartWatcher(razor.GetDirectory(), razor.GetName(), includeSubdirectories: false);
    }

    /// <summary>Watches every .razor / .razor.css / .razor.scss file under <paramref name="directory"/>.
    /// On change the components are re-registered and the current page is reloaded.
    /// SCSS sources are compiled to their adjacent CSS files before the reload, so
    /// edits to a .razor.scss file are visible without rebuilding the project.</summary>
    public void WatchDirectory(string directory)
    {
        StopWatching();
        var dir = FileSystemService.Default.ToUPath(directory);
        _watchDirectory = dir;
        _watchSnapshot = TakeDirectorySnapshot(dir);
        StartWatcher(dir, "*.razor*", includeSubdirectories: true);
    }

    /// <summary>
    /// Installs an <see cref="IFileSystemWatcher"/> as the primary change source.
    /// Events are debounced in <see cref="ProcessFileReload"/>; a throttled
    /// directory snapshot (every <see cref="PollInterval"/>) remains as a safety
    /// net for changes the watcher misses (network drives, editors that replace
    /// files without events, ...).
    /// </summary>
    private void StartWatcher(UPath directory, string filter, bool includeSubdirectories)
    {
        var fs = FileSystemService.Default;
        if (!fs.FileSystem.DirectoryExists(directory)) return;
        try
        {
            _watcher = fs.Watch(directory);
            _watcher.Filter = filter;
            _watcher.IncludeSubdirectories = includeSubdirectories;
            _watcher.NotifyFilter = Zio.NotifyFilters.LastWrite | Zio.NotifyFilters.FileName | Zio.NotifyFilters.Size | Zio.NotifyFilters.CreationTime;
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

    private void OnFileSystemEvent(object? sender, FileChangedEventArgs e)
    {
        _reloadRequested = true;
        _reloadNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(200);
    }

    /// <summary>How often the snapshot safety-net poll runs when a watcher is active (2 s).</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public void Update(float deltaTime = 1f / 60f)
    {
        // The watcher is the primary change source; the snapshot poll
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
            if (_watchDirectory is { } watchDirectory)
            {
                // Sass startup is expensive and must never run on the render
                // thread. Compile all entry points in one tool invocation, then
                // apply the resulting CSS on the next UI update.
                if (_scssCompileTask is null)
                {
                    _scssCompileTask = Task.Run(() => CompileScssFiles(watchDirectory));
                    return;
                }
                if (!_scssCompileTask.IsCompleted) return;
                _scssCompileTask.GetAwaiter().GetResult();
                _scssCompileTask = null;
                RegisterRazorComponentsFromDirectory(watchDirectory);
                if (_currentRoute is not null && _pages.Contains(_currentRoute)) Navigate(CurrentUrl);
                else if (_currentRoute is not null) { _currentRoute = null; ShowNotFound(CurrentUrl); }
                else if (_razorRoot is not null) _razorRenderPending = true;
                else if (_pages.Count > 0) Navigate(CurrentUrl);
            }
            else if (_razorPath is { } razorPath && FileSystemService.Default.FileExists(razorPath))
            {
                LoadRazorFromFile(razorPath, _razorClassName);
            }
            else if (_stylePath is { } stylePath && FileSystemService.Default.FileExists(stylePath))
            {
                if (_styleIsScoped)
                {
                    var scopeId = $"b-{_razorClassName.ToLowerInvariant()}";
                    LoadScopedStyles(stylePath.FullName, ReadStableText(stylePath), scopeId);
                }
                else
                {
                    LoadStyles(ReadStableText(stylePath));
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
        if (_watchDirectory is { } watchDirectory)
        {
            var snapshot = TakeDirectorySnapshot(watchDirectory);
            if (_watchSnapshot is null || !SnapshotEqual(_watchSnapshot, snapshot))
            {
                _watchSnapshot = snapshot;
                RequestReload(watchDirectory.GetName());
            }
            return;
        }
        if (_razorPath is { } razorPath)
        {
            var writeTime = GetWriteTime(razorPath);
            if (writeTime != _lastRazorWriteUtc)
            {
                _lastRazorWriteUtc = writeTime;
                RequestReload(razorPath.GetName());
            }
        }
        if (_stylePath is { } stylePath)
        {
            var writeTime = GetWriteTime(stylePath);
            if (writeTime != _lastStyleWriteUtc)
            {
                _lastStyleWriteUtc = writeTime;
                RequestReload(stylePath.GetName());
            }
        }
    }

    private void RequestReload(string name)
    {
        _reloadRequested = true;
        _reloadNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(200);
        Console.WriteLine($"[UI] Change detected: {name}");
    }

    private static DateTime GetWriteTime(UPath path) =>
        FileSystemService.Default.FileExists(path) ? FileSystemService.Default.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    /// <summary>
    /// Rebuilds the non-partial SCSS entry points before the directory reload.
    /// DartSassBuilder is already a project dependency; its command-line tool is
    /// used here so runtime compilation has exactly the same Sass semantics as a
    /// normal build (including @use imports and nesting).
    /// </summary>
    private static void CompileScssFiles(UPath directory)
    {
        var scssPaths = FileSystemService.Default.EnumerateFiles(directory, "*.scss", recursive: true)
            .Where(path => !path.GetName().StartsWith("_", StringComparison.Ordinal))
            .ToArray();
        if (scssPaths.Length == 0) return;
        if (!TryCompileScss(scssPaths))
            Console.WriteLine($"[UI] SCSS hot reload skipped: compiler unavailable for {scssPaths.Length} file(s).");
    }

    private static bool TryCompileScss(IReadOnlyList<UPath> scssPaths)
    {
        var fs = FileSystemService.Default;
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

        var outputPaths = scssPaths.Select(path => path.ChangeExtension(".css")).ToArray();
        var startInfo = new ProcessStartInfo
        {
            FileName = compiler.FileName,
            WorkingDirectory = fs.ToSystemPath(scssPaths[0].GetDirectory()),
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
                startInfo.ArgumentList.Add(fs.ToSystemPath(scssPath));
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
                startInfo.ArgumentList.Add(fs.ToSystemPath(pair.First));
                startInfo.ArgumentList.Add(fs.ToSystemPath(pair.Second));
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
                Console.WriteLine($"[UI] SCSS compiler timed out for {scssPaths[0].GetName()}.");
                return false;
            }

            if (process.ExitCode != 0)
            {
                _ = outputTask.GetAwaiter().GetResult();
                var error = errorTask.GetAwaiter().GetResult().Trim();
                Console.WriteLine($"[UI] SCSS compile failed for {scssPaths[0].GetName()}: {error}");
                return false;
            }
            _ = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            return outputPaths.All(fs.FileExists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Console.WriteLine($"[UI] SCSS compiler failed for {scssPaths[0].GetName()}: {ex.Message}");
            return false;
        }
    }

    private static SassCompilerCommand? FindSassCompiler()
    {
        var fs = FileSystemService.Default;
        var packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? PathUtil.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var toolDirectory = PathUtil.Combine(packageRoot, "dartsassbuilder", "1.1.0", "tool");
        var executable = OperatingSystem.IsWindows() ? "DartSassBuilder.exe" : "DartSassBuilder";
        var toolPath = PathUtil.Combine(toolDirectory, executable);
        if (fs.FileExists(toolPath)) return new SassCompilerCommand(toolPath, [], true);

        var toolDll = PathUtil.Combine(toolDirectory, "DartSassBuilder.dll");
        if (fs.FileExists(toolDll)) return new SassCompilerCommand("dotnet", [toolDll], true);

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
        foreach (var child in panel.ChildrenInternal) AdvanceCarets(child, deltaTime);
    }

    private static bool AdvanceAnimations(Panel panel, float deltaTime)
    {
        var animated = panel.AdvanceStyleAnimation(deltaTime);
        foreach (var child in panel.ChildrenInternal) animated |= AdvanceAnimations(child, deltaTime);
        return animated;
    }

    private static string ReadStableText(UPath path)
    {
        var fs = FileSystemService.Default;
        string? previous = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var stream = fs.OpenRead(path);
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
        return previous ?? fs.ReadAllText(path);
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

    private static Dictionary<string, DateTime> TakeDirectorySnapshot(UPath directory)
    {
        var snapshot = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var fs = FileSystemService.Default;
        if (!fs.FileSystem.DirectoryExists(directory)) return snapshot;
        foreach (var path in fs.EnumerateFiles(directory, "*.razor*", recursive: true))
            snapshot[path.FullName] = GetWriteTime(path);
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
