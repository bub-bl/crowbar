using System.Collections.Concurrent;
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
    /// Republishes the panel snapshot: every file under <c>/Content</c>,
    /// categorized by its top-level folder and the thumbnail kind of its
    /// registered asset type.
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
                    Path.GetFileName(logical),
                    CategoryOf(logical),
                    KindFor(logical)));
            }
        }
        catch (Exception ex)
        {
            // A project without a Content/ folder shows an empty panel.
            Log.Warn($"[Content] Could not enumerate the project content: {ex.Message}");
        }

        entries.Sort(static (a, b) =>
        {
            var category = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
            return category != 0 ? category : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
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

    /// <summary>The top-level folder of a logical content path ("Models/Crate/Crate.gltf" → "Models").</summary>
    private static string CategoryOf(string logical)
    {
        var slash = logical.IndexOf('/');
        return slash < 0 ? string.Empty : logical[..slash];
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

    private void OnChanged(object? sender, FileChangedEventArgs e) => _pending.Enqueue(e.FullPath);

    private void OnRenamed(object? sender, FileRenamedEventArgs e) => _pending.Enqueue(e.FullPath);
}
