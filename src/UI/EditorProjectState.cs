namespace Crowbar.UI;

/// <summary>
/// Live project state shown in the editor's top bar and its request channel
/// (UI → host). The host publishes the open game project's name and file
/// path; the top bar button requests opening a project through
/// <see cref="RequestOpen"/>, consumed by the host each frame exactly like
/// <see cref="EditorUndoState"/>.
///
/// This static only <em>mirrors</em> the project for the panels — the project
/// itself (<c>CrowbarProjectFile</c>) lives on the host, which also owns the
/// native file dialog and the project filesystem re-rooting.
/// </summary>
public static class EditorProjectState
{
    private static string _name = string.Empty;
    private static string _path = string.Empty;
    private static int _version;

    private static readonly Lock RequestLock = new();
    private static bool _openRequested;

    /// <summary>Name of the open project, or empty when the editor runs on the demo project.</summary>
    public static string Name => _name;

    /// <summary>Full path of the open <c>.crproj</c> file, or empty when the editor runs on the demo project.</summary>
    public static string Path => _path;

    /// <summary>True when a real project file is open (not the bare demo fallback).</summary>
    public static bool HasProject => _name.Length > 0;

    /// <summary>Bumped whenever the project name or path changes; the top bar hashes it.</summary>
    public static int Version => _version;

    /// <summary>Queues an open-project request (top bar click) for the host to consume this frame.</summary>
    public static void RequestOpen()
    {
        lock (RequestLock)
            _openRequested = true;
    }

    /// <summary>Returns and clears the pending open-project request, or false.</summary>
    public static bool ConsumeOpenRequest()
    {
        lock (RequestLock)
        {
            var requested = _openRequested;
            _openRequested = false;
            return requested;
        }
    }

    /// <summary>
    /// Replaces the published project; a no-op when nothing changed (the host
    /// publishes every frame, so identical frames must not force a rebuild).
    /// </summary>
    public static void Publish(string name, string path)
    {
        name ??= string.Empty;
        path ??= string.Empty;
        if (string.Equals(name, _name, StringComparison.Ordinal) &&
            string.Equals(path, _path, StringComparison.Ordinal))
            return;

        _name = name;
        _path = path;
        _version++;
    }
}