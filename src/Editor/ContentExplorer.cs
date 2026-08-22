using System.Collections.Concurrent;
using System.Diagnostics;
using Crowbar.Engine;
using Crowbar.Engine.Audio;
using Crowbar.FileSystems;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// The open project's content folder: builds the snapshot the Content panel
/// displays (from the <c>/Content</c> mount) and watches the folder for hot
/// reload. A changed asset is invalidated through the resource library's
/// extension registry (gltf → Model, png → Texture2D), so the next load
/// re-reads it, and every scene mesh renderer bound to it is re-resolved to
/// the fresh model — content edits show up without a restart. Owned by the
/// <see cref="Editor"/> instance like <see cref="GameProject"/>:
/// <see cref="Start"/> on startup and project switch, <see cref="Update"/>
/// every frame.
/// </summary>
public sealed class ContentExplorer : IDisposable
{
    private readonly ConcurrentQueue<FilePath> _pending = new();
    private IFileWatcher? _watcher;

    /// <summary>
    /// (Re)starts watching the current project's <c>Content/</c> folder: the
    /// previous watcher is disposed, the panel snapshot is republished from
    /// the current mount, and a recursive watcher is armed. A project without
    /// a content folder (or a backend that cannot watch) simply shows the
    /// empty snapshot.
    /// </summary>
    public void Start()
    {
        _watcher?.Dispose();
        _watcher = null;

        Publish();

        try
        {
            if (!FileSystem.Content.CanWatch("/Content"))
                return;

            var watcher = FileSystem.Content.Watch("/Content");
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = FileChangeFilters.FileName | FileChangeFilters.LastWrite;
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            Log.Warn($"[Content] Content watch unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the content changes detected since the last frame (call once
    /// per frame, on the main thread): each changed asset is invalidated via
    /// its registered type, scene mesh renderers bound to it are re-resolved
    /// to the freshly loaded model, and the panel snapshot is republished.
    /// </summary>
    public void Update()
    {
        ProcessAction();
        if (_pending.IsEmpty)
            return;

        var contentRoot = FileSystem.Content.ToFilePath("/Content");
        var reloaded = new HashSet<string>(StringComparer.Ordinal);
        var anyChange = false;
        while (_pending.TryDequeue(out var path))
        {
            if (ToLogicalPath(path, contentRoot) is not { } logical)
                continue;
            anyChange = true;
            if (ResourceLibrary.Invalidate(logical))
                reloaded.Add(logical);
        }

        if (!anyChange)
            return;
        // Any content change refreshes the panel (a new file must appear even
        // when it is not a registered asset); only reloaded assets re-resolve
        // their scene mesh renderers.
        if (reloaded.Count > 0)
            ReloadSceneModels(reloaded);
        Publish();
    }

    public void Dispose() => _watcher?.Dispose();

    /// <summary>
    /// Republishes the panel snapshot: every file under <c>/Content</c> with
    /// its logical path and the thumbnail kind of its registered asset type;
    /// the panel derives the folder structure from the paths.
    /// </summary>
    private static void Publish()
    {
        var entries = new List<EditorContentState.Entry>();
        try
        {
            var contentRoot = FileSystem.Content.ToFilePath("/Content");
            foreach (var file in FileSystem.Content.EnumerateFiles("/Content", "*", recursive: true))
            {
                if (ToLogicalPath(file, contentRoot) is not { } logical)
                    continue;
                entries.Add(new EditorContentState.Entry(
                    logical,
                    Path.GetFileName(logical),
                    KindFor(logical),
                    FileSystem.Content.GetLastWriteTimeUtc(file)));
            }
        }
        catch (Exception ex)
        {
            // A project without a Content/ folder shows an empty panel.
            Log.Warn($"[Content] Could not enumerate the project content: {ex.Message}");
        }

        entries.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        EditorContentState.Publish(entries);
    }

    /// <summary>
    /// Maps a watcher/enumeration path (a physical backend path) back to the
    /// logical <c>Content/...</c> form the resource library keys caches by, or
    /// null when the path is outside the content folder.
    /// </summary>
    private static string? ToLogicalPath(FilePath physical, FilePath contentRoot)
    {
        var prefix = contentRoot.FullName + "/";
        var full = physical.FullName;
        if (full.Length <= prefix.Length || !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return "Content/" + full[prefix.Length..];
    }

    /// <summary>Thumbnail class for a logical content path, from its registered asset type.</summary>
    private static string KindFor(string logical)
    {
        var type = ResourceLibrary.GetTypeForExtension(Path.GetExtension(logical));
        if (type == typeof(Model)) return "model";
        if (type == typeof(Texture2D)) return "texture";
        if (type == typeof(AudioClip)) return "sound";
        if (type == typeof(Shader)) return "shader";
        return "file";
    }

    /// <summary>
    /// Re-resolves every scene mesh renderer bound to a reloaded model path,
    /// so the fresh geometry is uploaded next frame (the invalidate disposed
    /// the previous instance).
    /// </summary>
    private static void ReloadSceneModels(HashSet<string> reloaded)
    {
        var world = Game.World;
        if (world is null)
            return;
        foreach (var renderer in world.Query<MeshRenderer>())
        {
            if (renderer.Model is not { } model || !reloaded.Contains(model.Path))
                continue;
            renderer.Model = ResourceLibrary.LoadModel(model.Path);
        }
    }

    private void ProcessAction()
    {
        if (!EditorContentState.TryConsumeAction(out var action))
            return;

        try
        {
            var contentPath = action.Path.StartsWith("Content/", StringComparison.OrdinalIgnoreCase)
                ? action.Path
                : "Content/" + action.Path.Trim('/');
            switch (action.Kind)
            {
                case EditorContentState.ActionKind.Open:
                    OpenPath(contentPath);
                    break;
                case EditorContentState.ActionKind.Rename:
                    if (FileSystem.Project.DirectoryExists(contentPath))
                        RenameDirectory(contentPath, action.Value);
                    else
                        RenameFile(contentPath, action.Value);
                    break;
                case EditorContentState.ActionKind.Delete:
                    if (FileSystem.Project.DirectoryExists(contentPath))
                        DeleteDirectory(contentPath);
                    else
                        DeleteFile(contentPath);
                    break;
                case EditorContentState.ActionKind.CreateFolder:
                    CreateDirectory(contentPath, action.Value);
                    break;
                case EditorContentState.ActionKind.CreateFile:
                    CreateFile(contentPath, action.Value);
                    break;
                case EditorContentState.ActionKind.Refresh:
                    Publish();
                    break;
                case EditorContentState.ActionKind.Reveal:
                    RevealPath(contentPath);
                    break;
            }
        }
        catch (Exception ex)
        {
            UiNotifications.Show("Content", $"Operation failed: {ex.Message}", "error");
        }
    }

    private static void OpenPath(string path)
    {
        if (!FileSystem.Project.FileExists(path))
        {
            UiNotifications.Show("Content", $"File not found: {path}", "error");
            return;
        }

        var process = Process.Start(new ProcessStartInfo(FileSystem.Project.ToSystemPath(path))
        {
            UseShellExecute = true
        });
        if (process is null)
            UiNotifications.Show("Content", "Could not open the file.", "error");
    }

    private static void RenameFile(string path, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName is "." or ".." || Path.GetFileName(newName) != newName ||
            newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            UiNotifications.Show("Content", "Enter a valid file name.", "error");
            return;
        }

        if (!FileSystem.Project.FileExists(path))
        {
            UiNotifications.Show("Content", $"File not found: {path}", "error");
            return;
        }

        var directory = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
        var destination = directory.Length == 0 ? newName : directory + "/" + newName;
        FileSystem.Project.MoveFile(path, destination);
        UiNotifications.Show("Content", $"Renamed to {newName}", "success");
    }

    private static void DeleteFile(string path)
    {
        if (!FileSystem.Project.FileExists(path))
        {
            UiNotifications.Show("Content", $"File not found: {path}", "error");
            return;
        }

        FileSystem.Project.DeleteFile(path);
        UiNotifications.Show("Content", $"Deleted {Path.GetFileName(path)}", "success");
    }

    private static void CreateDirectory(string parentPath, string name)
    {
        if (!IsValidName(name))
        {
            UiNotifications.Show("Content", "Enter a valid folder name.", "error");
            return;
        }

        var destination = parentPath.TrimEnd('/') + "/" + name;
        if (FileSystem.Project.FileExists(destination) || FileSystem.Project.DirectoryExists(destination))
        {
            UiNotifications.Show("Content", $"An item named {name} already exists.", "error");
            return;
        }

        FileSystem.Project.CreateDirectory(destination);
        UiNotifications.Show("Content", $"Created folder {name}", "success");
    }

    private static void CreateFile(string parentPath, string name)
    {
        name = name.Trim();
        if (!IsValidName(name))
        {
            UiNotifications.Show("Content", "Enter a valid file name.", "error");
            return;
        }

        // A bare name without an extension becomes a text file.
        if (Path.GetExtension(name).Length == 0)
            name += ".txt";

        var destination = parentPath.TrimEnd('/') + "/" + name;
        if (FileSystem.Project.FileExists(destination) || FileSystem.Project.DirectoryExists(destination))
        {
            UiNotifications.Show("Content", $"An item named {name} already exists.", "error");
            return;
        }

        FileSystem.Project.WriteAllBytes(destination, []);
        UiNotifications.Show("Content", $"Created file {name}", "success");
    }

    private static void RenameDirectory(string path, string newName)
    {
        if (!IsValidName(newName))
        {
            UiNotifications.Show("Content", "Enter a valid folder name.", "error");
            return;
        }

        var source = FileSystem.Project.ToSystemPath(path);
        var parent = Directory.GetParent(source)?.FullName;
        if (parent is null)
            throw new IOException("The folder has no valid parent.");
        var destination = Path.Combine(parent, newName);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            UiNotifications.Show("Content", $"An item named {newName} already exists.", "error");
            return;
        }

        Directory.Move(source, destination);
        UiNotifications.Show("Content", $"Renamed folder to {newName}", "success");
    }

    private static void DeleteDirectory(string path)
    {
        if (!FileSystem.Project.DirectoryExists(path))
        {
            UiNotifications.Show("Content", $"Folder not found: {path}", "error");
            return;
        }

        Directory.Delete(FileSystem.Project.ToSystemPath(path), recursive: true);
        UiNotifications.Show("Content", $"Deleted folder {Path.GetFileName(path.TrimEnd('/'))}", "success");
    }

    private static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim() == name && name is not "." and not ".." &&
        Path.GetFileName(name) == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void RevealPath(string path)
    {
        var physical = FileSystem.Project.ToSystemPath(path);
        var directory = Directory.Exists(physical) ? physical : Path.GetDirectoryName(physical) ?? physical;
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{physical}\"")
            {
                UseShellExecute = true
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo("xdg-open", directory)
            {
                UseShellExecute = false
            });
        }
    }

    private void OnChanged(object? sender, FileChangedEventArgs e) => _pending.Enqueue(e.FullPath);

    private void OnRenamed(object? sender, FileRenamedEventArgs e) => _pending.Enqueue(e.FullPath);
}
