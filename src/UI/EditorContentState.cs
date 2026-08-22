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
    public readonly record struct Entry(string Path, string Name, string Kind, DateTime Modified = default);

    public enum ActionKind
    {
        Open,
        Rename,
        Delete,
        Reveal,
        CreateFolder,
        CreateFile,
        Refresh
    }

    public readonly record struct ActionRequest(ActionKind Kind, string Path, string Value = "");

    /// <summary>
    /// An open context menu. <see cref="IsEmpty"/> is true for the menu opened
    /// by a right-click on the content panel's empty area: <see cref="Path"/>
    /// then carries the folder being browsed (the target of its new-item
    /// actions), not an item the menu acts on.
    /// </summary>
    public readonly record struct ContextMenuState(string Path, bool IsFolder, float X, float Y, bool IsEmpty = false);

    private static IReadOnlyList<Entry> _entries = [];
    private static string _currentFolder = string.Empty;
    private static readonly List<string> _history = [string.Empty];
    private static int _historyIndex;
    private static string _searchQuery = string.Empty;
    private static string? _selectedPath;
    private static string _viewMode = "grid";
    private static string _sortMode = "name";
    private static int _tileScale = 1;
    private static float _sidebarWidth = 160;
    private static ContextMenuState? _contextMenu;
    private static ActionRequest? _pendingAction;
    // Folders the user expanded and folders the user collapsed in the sidebar
    // tree. IsOpen consults the collapse set first, so an explicit collapse
    // wins over the always-open path rule: a folder on the browsed path (or
    // the browsed folder itself) can be folded.
    private static readonly HashSet<string> Expanded = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Collapsed = new(StringComparer.Ordinal);
    private static int _version;

    /// <summary>Raised whenever content browser state changes so the top-level overlay can refresh.</summary>
    public static event Action? Changed;

    private static void BumpVersion()
    {
        _version++;
        Changed?.Invoke();
    }

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

    /// <summary>Current content search query.</summary>
    public static string SearchQuery => _searchQuery;

    /// <summary>Logical path of the currently selected asset or folder.</summary>
    public static string? SelectedPath => _selectedPath;

    /// <summary>Current content display mode: grid or list.</summary>
    public static string ViewMode => _viewMode;

    /// <summary>Current content sort mode: name, type or modified.</summary>
    public static string SortMode => _sortMode;

    /// <summary>Content tile scale: 0 (compact), 1 (medium), or 2 (large).</summary>
    public static int TileScale => _tileScale;

    /// <summary>Content sidebar width in pixels.</summary>
    public static float SidebarWidth => _sidebarWidth;

    /// <summary>Whether a previous or next folder is available.</summary>
    public static bool CanGoBack => _historyIndex > 0;
    public static bool CanGoForward => _historyIndex < _history.Count - 1;

    /// <summary>Currently open context menu, if any.</summary>
    public static ContextMenuState? ContextMenu => _contextMenu;

    /// <summary>
    /// Replaces the snapshot, bumping <see cref="Version"/> only when the list
    /// actually changed (the host republishes on every content change, so a
    /// no-op must not force a panel rebuild).
    /// </summary>
    public static void Publish(IReadOnlyList<Entry> entries)
    {
        if (entries.SequenceEqual(_entries)) return;
        _entries = entries;
        BumpVersion();
    }

    /// <summary>
    /// Browses into <paramref name="folder"/> (a content-relative folder path
    /// like <c>Models/Crate</c>; empty or <c>null</c> returns to the content
    /// root), bumping <see cref="Version"/> so the panel re-renders. The
    /// destination and its ancestors are revealed: navigation makes the
    /// browsed path visible in the tree, dropping any earlier collapse.
    /// </summary>
    public static void SetSearchQuery(string query)
    {
        query ??= string.Empty;
        if (string.Equals(_searchQuery, query, StringComparison.Ordinal)) return;
        _searchQuery = query;
        BumpVersion();
    }

    public static void Select(string path)
    {
        path = (path ?? string.Empty).Trim('/');
        if (path.Length == 0 || string.Equals(_selectedPath, path, StringComparison.Ordinal)) return;
        _selectedPath = path;
        CloseContextMenu();
        BumpVersion();
    }

    public static void ClearSelection()
    {
        if (_selectedPath is null) return;
        _selectedPath = null;
        BumpVersion();
    }

    public static void SetViewMode(string mode)
    {
        mode = mode.Equals("list", StringComparison.OrdinalIgnoreCase) ? "list" : "grid";
        if (_viewMode == mode) return;
        _viewMode = mode;
        BumpVersion();
    }

    public static void CycleSortMode()
    {
        _sortMode = _sortMode switch
        {
            "name" => "type",
            "type" => "modified",
            _ => "name"
        };
        BumpVersion();
    }

    public static void AdjustTileScale(int delta)
    {
        var next = Math.Clamp(_tileScale + delta, 0, 2);
        if (next == _tileScale) return;
        _tileScale = next;
        BumpVersion();
    }

    /// <summary>Sets the tile scale directly (0 compact, 1 medium, 2 large), clamped to the valid range.</summary>
    public static void SetTileScale(int scale)
    {
        var next = Math.Clamp(scale, 0, 2);
        if (_tileScale == next) return;
        _tileScale = next;
        BumpVersion();
    }

    public static void SetSidebarWidth(float width)
    {
        var next = Math.Clamp(width, 120f, 320f);
        if (Math.Abs(next - _sidebarWidth) < 0.5f) return;
        _sidebarWidth = next;
        BumpVersion();
    }

    public static void NavigateTo(string folder)
    {
        folder = (folder ?? string.Empty).Trim('/');
        var navigated = !string.Equals(_currentFolder, folder, StringComparison.Ordinal);
        var revealed = Reveal(folder);
        if (!navigated && !revealed) return;
        if (navigated)
        {
            if (_historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(folder);
            _historyIndex++;
        }
        _currentFolder = folder;
        CloseContextMenu();
        BumpVersion();
    }

    public static void GoBack()
    {
        if (!CanGoBack) return;
        _historyIndex--;
        _currentFolder = _history[_historyIndex];
        Reveal(_currentFolder);
        CloseContextMenu();
        BumpVersion();
    }

    public static void GoForward()
    {
        if (!CanGoForward) return;
        _historyIndex++;
        _currentFolder = _history[_historyIndex];
        Reveal(_currentFolder);
        CloseContextMenu();
        BumpVersion();
    }

    public static void OpenContextMenu(string path, bool isFolder, float x, float y)
    {
        _contextMenu = new ContextMenuState(path, isFolder, x, y);
        BumpVersion();
    }

    /// <summary>
    /// Opens the empty-area context menu for <paramref name="folder"/> (the
    /// folder being browsed, "" for the content root): its actions create new
    /// items inside that folder.
    /// </summary>
    public static void OpenEmptyContextMenu(string folder, float x, float y)
    {
        _contextMenu = new ContextMenuState((folder ?? string.Empty).Trim('/'), true, x, y, IsEmpty: true);
        BumpVersion();
    }

    public static void CloseContextMenu()
    {
        if (_contextMenu is null) return;
        _contextMenu = null;
        BumpVersion();
    }

    public static void RequestAction(ActionKind kind, string path, string value = "")
    {
        _pendingAction = new ActionRequest(kind, path, value);
        CloseContextMenu();
        BumpVersion();
    }

    public static bool TryConsumeAction(out ActionRequest action)
    {
        if (_pendingAction is not { } pending)
        {
            action = default;
            return false;
        }

        _pendingAction = null;
        action = pending;
        return true;
    }

    public static void SetAllExpanded(IEnumerable<string> folders, bool expanded)
    {
        foreach (var folder in folders)
        {
            if (expanded)
            {
                Expanded.Add(folder);
                Collapsed.Remove(folder);
            }
            else
            {
                Expanded.Remove(folder);
                if (folder.Length > 0) Collapsed.Add(folder);
            }
        }
        BumpVersion();
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

        BumpVersion();
    }

    /// <summary>Clears the snapshot, the tree state and returns to the content root (teardown between tests).</summary>
    public static void Reset()
    {
        Expanded.Clear();
        Collapsed.Clear();
        _history.Clear();
        _history.Add(string.Empty);
        _historyIndex = 0;
        _currentFolder = string.Empty;
        _searchQuery = string.Empty;
        _selectedPath = null;
        _viewMode = "grid";
        _sortMode = "name";
        _tileScale = 1;
        _sidebarWidth = 160;
        _contextMenu = null;
        _pendingAction = null;
        Publish([]);
        BumpVersion();
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
