namespace Crowbar.UI;

/// <summary>
/// Mirror of the open project's content folder (the <c>Content/</c> directory
/// mounted at <c>/Content</c>) that the Content panel displays. The editor
/// host owns the filesystem and republishes the file list on startup, on
/// project switch and whenever the content watcher reports a change; the
/// panel itself is UI-only and never touches engine types, keeping the UI
/// assembly a leaf (Engine → UI, like <see cref="EditorExplorerState"/>).
/// </summary>
public static class EditorContentState
{
    /// <summary>
    /// One file in the content folder. <see cref="Path"/> is the file's
    /// logical content path (<c>Content/Models/Crate/Crate.gltf</c>), from
    /// which the panel derives the folder structure; <see cref="Name"/> is the
    /// file name; <see cref="Kind"/> is the thumbnail class the panel renders
    /// (model/texture/sound/shader/file), derived from the file's registered
    /// asset type.
    /// </summary>
    public readonly record struct Entry(string Path, string Name, string Kind);

    private static IReadOnlyList<Entry> _entries = [];
    private static string _currentFolder = string.Empty;
    // Folders the user expanded in the sidebar tree; the path to the browsed
    // folder stays open regardless (see the panel's IsOpen).
    private static readonly HashSet<string> Expanded = new(StringComparer.Ordinal);
    private static int _version;

    /// <summary>Flat recursive file list of the open project's content folder.</summary>
    public static IReadOnlyList<Entry> Entries => _entries;

    /// <summary>
    /// The folder the panel is browsing, relative to the content root
    /// (<c>Models/Crate</c>), empty for the content root itself.
    /// </summary>
    public static string CurrentFolder => _currentFolder;

    /// <summary>Segments of <see cref="CurrentFolder"/> for the breadcrumb (<c>["Models", "Crate"]</c>).</summary>
    public static IReadOnlyList<string> Breadcrumb =>
        _currentFolder.Length == 0 ? [] : _currentFolder.Split('/');

    /// <summary>
    /// Bumped whenever the snapshot or the browsed folder changes; the Content
    /// panel hashes it so it re-renders only then.
    /// </summary>
    public static int Version => _version;

    /// <summary>
    /// Replaces the snapshot, bumping <see cref="Version"/> only when the list
    /// actually changed (the host republishes on every content change, so a
    /// no-op must not force a panel rebuild).
    /// </summary>
    public static void Publish(IReadOnlyList<Entry> entries)
    {
        if (entries.SequenceEqual(_entries)) return;
        _entries = entries;
        _version++;
    }

    /// <summary>
    /// Browses into <paramref name="folder"/> (a content-relative folder path
    /// like <c>Models/Crate</c>; empty or <c>null</c> returns to the content
    /// root), bumping <see cref="Version"/> so the panel re-renders.
    /// </summary>
    public static void NavigateTo(string folder)
    {
        folder = (folder ?? string.Empty).Trim('/');
        if (string.Equals(_currentFolder, folder, StringComparison.Ordinal)) return;
        _currentFolder = folder;
        _version++;
    }

    /// <summary>True when the user expanded <paramref name="folder"/> in the sidebar tree.</summary>
    public static bool IsExpanded(string folder) => Expanded.Contains(folder);

    /// <summary>Toggles the expanded state of <paramref name="folder"/>, bumping <see cref="Version"/>.</summary>
    public static void ToggleExpanded(string folder)
    {
        if (!Expanded.Add(folder)) Expanded.Remove(folder);
        _version++;
    }

    /// <summary>Clears the snapshot, the expansion set and returns to the content root (teardown between tests).</summary>
    public static void Reset()
    {
        Expanded.Clear();
        NavigateTo(string.Empty);
        Publish([]);
    }
}
