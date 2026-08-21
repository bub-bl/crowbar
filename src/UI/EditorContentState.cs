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
    // Folders the user expanded and folders the user collapsed in the sidebar
    // tree. IsOpen consults the collapse set first, so an explicit collapse
    // wins over the always-open path rule: a folder on the browsed path (or
    // the browsed folder itself) can be folded.
    private static readonly HashSet<string> Expanded = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Collapsed = new(StringComparer.Ordinal);
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
    /// Bumped whenever the snapshot, the browsed folder or a tree expansion
    /// changes; the Content panel hashes it so it re-renders only then.
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
    /// root), bumping <see cref="Version"/> so the panel re-renders. The
    /// destination and its ancestors are revealed: navigation makes the
    /// browsed path visible in the tree, dropping any earlier collapse.
    /// </summary>
    public static void NavigateTo(string folder)
    {
        folder = (folder ?? string.Empty).Trim('/');
        var navigated = !string.Equals(_currentFolder, folder, StringComparison.Ordinal);
        var revealed = Reveal(folder);
        if (!navigated && !revealed) return;
        _currentFolder = folder;
        _version++;
    }

    /// <summary>
    /// True when <paramref name="folder"/> is open in the sidebar tree. An
    /// explicit collapse wins; otherwise the folder is open when the user
    /// expanded it or when it is the browsed folder or an ancestor of it (the
    /// path to the browsed folder is visible by default). The content root is
    /// always open unless explicitly collapsed.
    /// </summary>
    public static bool IsOpen(string folder) =>
        !Collapsed.Contains(folder) &&
        (folder.Length == 0 || Expanded.Contains(folder) ||
         folder == _currentFolder || _currentFolder.StartsWith(folder + "/", StringComparison.Ordinal));

    /// <summary>
    /// Toggles the open/closed state of <paramref name="folder"/> (the sidebar
    /// caret), bumping <see cref="Version"/>. Collapsing records the folder in
    /// the collapse set so the path rule cannot reopen it; expanding removes
    /// it again.
    /// </summary>
    public static void ToggleExpanded(string folder)
    {
        folder = (folder ?? string.Empty).Trim('/');
        if (IsOpen(folder))
        {
            Collapsed.Add(folder);
            Expanded.Remove(folder);
        }
        else
        {
            Expanded.Add(folder);
            Collapsed.Remove(folder);
        }

        _version++;
    }

    /// <summary>Clears the snapshot, the tree state and returns to the content root (teardown between tests).</summary>
    public static void Reset()
    {
        Expanded.Clear();
        Collapsed.Clear();
        NavigateTo(string.Empty);
        Publish([]);
    }

    /// <summary>
    /// Marks <paramref name="folder"/> and its ancestors as user-expanded,
    /// dropping any earlier collapse — navigation makes the browsed path
    /// visible in the tree. Returns true when the expansion state changed.
    /// </summary>
    private static bool Reveal(string folder)
    {
        var changed = false;
        var running = string.Empty;
        foreach (var segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            running = running.Length == 0 ? segment : running + "/" + segment;
            if (Collapsed.Remove(running)) changed = true;
            if (Expanded.Add(running)) changed = true;
        }

        // The content root is open by default; navigating back to it drops a
        // collapse the user may have set on it.
        if (folder.Length == 0 && Collapsed.Remove(string.Empty)) changed = true;
        return changed;
    }
}
