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
    /// One file in the content folder. <see cref="Category"/> is the top-level
    /// folder (e.g. "Models"), empty for files at the content root;
    /// <see cref="Kind"/> is the thumbnail class the panel renders
    /// (model/texture/sound/shader/file), derived from the file's registered
    /// asset type.
    /// </summary>
    public readonly record struct Entry(string Name, string Category, string Kind);

    private static IReadOnlyList<Entry> _entries = [];
    private static string _selectedCategory = string.Empty;
    private static int _version;

    /// <summary>Flat file list of the open project's content folder.</summary>
    public static IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Content category selected in the panel sidebar ("" = the content root).</summary>
    public static string SelectedCategory => _selectedCategory;

    /// <summary>
    /// Bumped whenever the snapshot changes; the Content panel hashes it so it
    /// re-renders only then.
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
    /// Selects a content category in the panel sidebar ("" = the content
    /// root), bumping <see cref="Version"/> so the panel re-renders.
    /// </summary>
    public static void SelectCategory(string category)
    {
        category ??= string.Empty;
        if (string.Equals(_selectedCategory, category, StringComparison.Ordinal)) return;
        _selectedCategory = category;
        _version++;
    }

    /// <summary>Clears the snapshot and the sidebar selection (teardown between tests).</summary>
    public static void Reset()
    {
        SelectCategory(string.Empty);
        Publish([]);
    }
}
