namespace Crowbar.UI;

/// <summary>
/// Live document state shown in the editor's custom title bar. The host writes
/// the open document name and its dirty flag every frame; the TopBar hashes
/// <see cref="Version"/> so it re-renders only when the document changes.
/// Kept in the UI assembly (like <see cref="UiDiagnostics"/> and
/// <see cref="EditorExplorerState"/>) so panels never depend on engine types.
/// </summary>
public static class EditorDocumentState
{
    private static string _title = string.Empty;
    private static bool _isDirty;
    private static int _version;

    /// <summary>Name of the open document, or empty when no document is open.</summary>
    public static string Title => _title;

    /// <summary>True when the document has unsaved changes.</summary>
    public static bool IsDirty => _isDirty;

    /// <summary>Bumped whenever the title or dirty flag changes.</summary>
    public static int Version => _version;

    /// <summary>True when a document is open (the title bar shows its name instead of the app name).</summary>
    public static bool HasDocument => _title.Length > 0;

    /// <summary>Publishes the current document; a no-op when nothing changed.</summary>
    public static void Publish(string title, bool isDirty)
    {
        title ??= string.Empty;
        if (string.Equals(title, _title, StringComparison.Ordinal) && isDirty == _isDirty) return;
        _title = title;
        _isDirty = isDirty;
        _version++;
    }
}
