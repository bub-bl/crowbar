using Crowbar.Engine;
using Crowbar.FileSystems;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// The open <c>.crproj</c> project: loaded from the command line (double-click
/// launch), picked through the native dialog, or switched to at runtime. Owned
/// by the <see cref="Editor"/> instance. It publishes the top-bar state through
/// <see cref="EditorProjectState"/> and owns the project name that drives the
/// saved level file (<see cref="LevelFileName"/>). Re-rooting the project
/// filesystem is handled by <see cref="Editor.ApplyProjectRoot"/>; the game
/// project (scripts) lifecycle lives in <see cref="GameProject"/>.
/// </summary>
public sealed class Project
{
    private readonly Editor _editor;
    private CrowbarProjectFile? _file;
    private string? _filePath;

    public Project(Editor editor) => _editor = editor;

    /// <summary>The open project file, or null on the bare demo project.</summary>
    public CrowbarProjectFile? File => _file;

    /// <summary>Full path of the open <c>.crproj</c> file, or null in a bare start.</summary>
    public string? FilePath => _filePath;

    /// <summary>Name of the open project, or empty when the editor runs on the demo project.</summary>
    public string Name => _file?.Name ?? string.Empty;

    /// <summary>
    /// The project-relative path the open level is saved to and loaded from
    /// (Ctrl+S). Levels are project content, so they live in the project's
    /// <c>Content/</c> folder; each project carries its own level named after
    /// it, and the bare demo run falls back to "Content/Demo.level".
    /// </summary>
    public string LevelFileName =>
        _file is { Name.Length: > 0 } project ? $"Content/{project.Name}.level" : "Content/Demo.level";

    /// <summary>
    /// Opens the <c>.crproj</c> given on the command line (double-click launch).
    /// Its directory was already chosen as the project filesystem root by
    /// <see cref="Editor.ConfigureFileSystem"/>, so it loads through the
    /// project filesystem by its file name. A missing or unreadable file leaves
    /// the editor on the bare demo project and surfaces an error notification.
    /// </summary>
    public void LoadStartup(string? projectFilePath)
    {
        if (projectFilePath is null)
            return;

        try
        {
            _file = CrowbarProjectFile.Load(Path.GetFileName(projectFilePath));
            _filePath = projectFilePath;
            Log.Info($"[Project] Opened '{projectFilePath}': {_file.Name} v{_file.Version}.");
            Publish();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Project] Failed to load '{projectFilePath}': {ex.Message}");
            UiNotifications.Show("Project", $"Unreadable project: {Path.GetFileName(projectFilePath)}", "error");
        }
    }

    /// <summary>
    /// Opens a native Explorer dialog to pick a <c>.crproj</c> file. Returns the
    /// chosen path, or null when the user cancelled.
    /// </summary>
    public string? PickFromDialog()
    {
        string? initialDirectory = null;
        if (_filePath is not null)
            initialDirectory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrEmpty(initialDirectory))
            initialDirectory = FileSystem.Project.ContentRoot;

        return NativeFileDialog.PickCrproj(_editor.Window.NativeHandle, initialDirectory);
    }

    /// <summary>
    /// Parses the <c>.crproj</c> at <paramref name="projectFilePath"/> without
    /// switching yet. Returns the project, or null (with an error notification)
    /// when the file is unreadable — the current project stays active.
    /// </summary>
    public CrowbarProjectFile? BeginSwitch(string projectFilePath)
    {
        try
        {
            return CrowbarProjectFile.LoadFromDisk(projectFilePath);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Project] Failed to open '{projectFilePath}': {ex.Message}");
            UiNotifications.Show("Project", $"Unreadable project: {Path.GetFileName(projectFilePath)}", "error");
            return null;
        }
    }

    /// <summary>Makes the parsed project the open one (after the filesystem was re-rooted).</summary>
    public void Commit(CrowbarProjectFile project, string projectFilePath)
    {
        _file = project;
        _filePath = projectFilePath;
    }

    /// <summary>Publishes the project to the top bar; a no-op when nothing changed.</summary>
    public void Publish() =>
        EditorProjectState.Publish(_file?.Name ?? string.Empty, _filePath ?? string.Empty);
}
