using System.Diagnostics;
using Crowbar.Engine;
using Crowbar.Engine.InputSystem;
using Crowbar.Engine.Platform;
using Crowbar.Engine.Rendering;
using Crowbar.FileSystems;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// The editor host: the engine <see cref="Application"/> session that runs the
/// editor. It is the composition root — it owns the startup order, drives the
/// per-frame update phases and the project-switch workflow, and publishes the
/// live engine state through <see cref="Current"/> so the editor API
/// (<see cref="Game"/>, <see cref="Viewport"/>, ...) reaches the World/Renderer/
/// UI without dependency injection. The actual work lives in the editor
/// statics; this class only wires them together.
/// </summary>
internal sealed class EditorHost : Application
{
    private readonly string? _startupProjectFile;
    private string _lastWindowTitle = string.Empty;

    /// <summary>The live editor host, set at startup and cleared on dispose.</summary>
    internal static EditorHost? Current { get; private set; }

    internal EditorHost(string? projectFilePath = null)
    {
        _startupProjectFile = projectFilePath;
    }

    // The engine exposes Renderer/Platform/OpenWindow as protected session
    // members; the editor statics reach them through these internal accessors.
    internal Renderer? HostRenderer => Renderer;
    internal IPlatform HostPlatform => Platform;
    internal WindowSession OpenEditorWindow(WindowOptions options) => OpenWindow(options);

    protected override void OnInitialize()
    {
        Current = this;

        // The translation gizmo snap follows the grid cell size.
        Viewport.ConfigureSnap();

        // The .crproj passed on the command line (double-click launch) is
        // opened first: its directory is the project root the filesystem was
        // configured with, and its name drives the window title and the level
        // file name. A broken file falls back to the bare demo project instead
        // of blocking startup.
        Project.LoadStartup(_startupProjectFile);

        // The game project must start before the level is loaded: only its
        // registered component types resolve when a saved document is
        // materialized below.
        GameProject.Start();

        // Persistence: the project's saved level is loaded when it exists (so
        // edits survive a restart), otherwise the demo scene is built from
        // scratch. Either way the open level starts clean — the title bar's
        // "●" appears only once a mutation marks the level dirty (Level.IsDirty).
        Game.LoadLevel();
        World!.Start();
        Log.Info($"[World] {Game.Level!.Entities.Count} entit(ies) in level '{Game.Level.Name}'.");

        // Initial selection: the main cube (or the first mesh in a loaded
        // level), to show the widget.
        Viewport.SelectInitial(Game.Level);

        // Editor shortcuts: Ctrl+S saves, Ctrl+Z undoes, Ctrl+Shift+Z (and
        // Ctrl+Y) redo, Ctrl+Shift+N toggles the notification window.
        EditorShortcuts.Register();
        // Publishes the initial explorer/inspector and subscribes to level
        // restores (undo/redo) so the selection stays coherent.
        Viewport.Initialize();

        // The editor page lives in the primary window; the notification popup
        // registers the same components and navigates to its own page — the
        // template compilation is globally cached, so registering per window is
        // cheap and hot reload stays per window.
        ConfigureUiForWindow(Ui, "/editor");

        // The persistent notification window (bottom-left, shows compilation
        // results): created once at startup, never destroyed — only hidden.
        NotificationWindow.EnsureCreated();
    }

    protected override void OnUpdate(float deltaTime)
    {
        base.OnUpdate(deltaTime);

        // Fire the registered editor shortcuts (Ctrl+S saves, Ctrl+Z undoes,
        // Ctrl+Shift+Z/Ctrl+Y redo); Input.Poll already ran this frame, so the
        // press edges are fresh.
        EditorShortcuts.Update();

        // UI → host requests are applied before the frame's edits, so a click
        // lands immediately on the state the button displayed.
        Game.UpdateRequests(); // undo/redo from the toolbar
        if (EditorProjectState.ConsumeOpenRequest())
            OpenProjectFromDialog(); // modal native dialog, main thread
        if (EditorNotificationWindowState.ConsumeToggleRequest())
            NotificationWindow.Toggle();

        // Live state for the top bar, the custom title bar and the OS title.
        Project.Publish();
        Game.PublishLevelState();
        SyncWindowTitle();

        // Live values for the editor status bar (FPS, memory, latency, game entries).
        PublishDiagnostics(deltaTime);

        // Script host: applies detected hot reloads and prunes the toasts.
        GameProject.Update();

        // Viewport interaction: gizmos, picking, selection, inspector/explorer.
        Viewport.Update(deltaTime, ViewportWidth, ViewportHeight);

        Game.PublishUndoState();
        Game.DetectUnbracketedMutations();
    }

    /// <summary>
    /// The camera orbit only starts when the right press begins inside the
    /// viewport and is not consumed by the UI (toolbar button, tab, input,
    /// scrollbar, overlay). The decision is made at press time: a drag
    /// engaged in the viewport continues even if the cursor moves over a
    /// panel, and a press on the UI never orbits.
    /// </summary>
    protected override bool CanMouseLook() =>
        !Ui.PointerPressConsumed && Viewport.ContainsPointer(ViewportWidth, ViewportHeight);

    /// <summary>
    /// While typing in a field (TextInput), the ZQSD/space keys go back to
    /// the field: the camera must not move at the same time. While a
    /// modifier is held (Ctrl+Z, Ctrl+S, Ctrl+Shift+Z, …) the movement keys
    /// are shortcut keys: holding Ctrl and pressing Z must undo, not walk
    /// the camera forward.
    /// </summary>
    protected override bool CanMoveCamera() =>
        !Ui.KeyboardConsumed && Input.HeldModifiers() == KeyModifiers.None;

    /// <summary>
    /// The wheel zooms the camera only when the cursor is inside the viewport
    /// and off the UI: over a scrollable panel or an interactive element
    /// (toolbar, tab), the wheel stays with the UI.
    /// </summary>
    protected override bool CanZoomCamera() =>
        Viewport.ContainsPointer(ViewportWidth, ViewportHeight) && !Ui.WheelConsumed;

    /// <summary>
    /// Pan (middle mouse) only starts inside the viewport and off the UI,
    /// like the orbit: a press on a panel never moves the scene.
    /// </summary>
    protected override bool CanPan() =>
        Viewport.ContainsPointer(ViewportWidth, ViewportHeight) && !Ui.PointerPressConsumed;

    /// <summary>
    /// While orbiting, the mouse stays confined inside the viewport: even
    /// hidden, it must not slide over the neighbouring panels (it would
    /// reappear off-scene on release). The orbit delta stays unlimited
    /// (measured before the confinement): pushing against an edge keeps
    /// rotating, only the cursor is held inside the scene.
    /// </summary>
    protected override UiRect? MouseLookClampRect => Viewport.Rect(ViewportWidth, ViewportHeight);

    /// <summary>
    /// Opens a native Explorer dialog to pick a <c>.crproj</c> file, then
    /// switches the editor to that project (filesystem root, document, game
    /// project).
    /// </summary>
    private void OpenProjectFromDialog()
    {
        var path = Project.PickFromDialog();
        if (path is null)
            return; // user cancelled
        SwitchProject(path);
    }

    /// <summary>
    /// Switches the editor to the game project described by
    /// <paramref name="projectFilePath"/>: re-roots the project filesystem at the
    /// file's directory, reloads the document (level) and the game project from
    /// that root, and refreshes every published panel state. The current document
    /// is discarded; a broken project keeps the previous one and reports an error.
    /// </summary>
    private void SwitchProject(string projectFilePath)
    {
        // Parse first: a broken project keeps the previous one active.
        if (Project.BeginSwitch(projectFilePath) is not { } project)
            return;

        var projectRoot = Path.GetDirectoryName(projectFilePath);
        if (string.IsNullOrEmpty(projectRoot))
            return;

        // Stop play mode before tearing the document down.
        World!.Stop();

        // Re-root the project filesystem at the new project's directory. Levels,
        // game project scripts and every save resolve through FileSystem.Project,
        // so this one call redirects all subsequent reads/writes.
        ApplyProjectRoot(projectRoot);

        Project.Commit(project, projectFilePath);

        // Reload the game project from the new root first: its component types
        // must be registered before the document is materialized, so saved
        // components resolve when ReloadLevel loads the level.
        GameProject.Start();
        Game.ReloadLevel();
        Viewport.ResetSelection();
        Project.Publish();

        Log.Info($"[Project] Project opened: {project.Name} v{project.Version} from {projectFilePath}");
        UiNotifications.Show("Project", $"Project opened: {project.Name}", "success");
    }

    /// <summary>
    /// Pushes the composed window title (document name + dirty flag) to the OS
    /// only when it changed, so the platform title is not set every frame.
    /// </summary>
    private void SyncWindowTitle()
    {
        var title = ComposeWindowTitle();
        if (string.Equals(title, _lastWindowTitle, StringComparison.Ordinal)) return;
        _lastWindowTitle = title;
        Window.SetTitle(title);
    }

    /// <summary>Composes the OS window title: project, then the open document and its dirty flag.</summary>
    private string ComposeWindowTitle()
    {
        var head = Project.Name.Length > 0 ? $"Crowbar — {Project.Name}" : $"Crowbar — {Ui.CurrentUrl}";
        var doc = EditorDocumentState.Title;
        if (doc.Length == 0) return head;
        return EditorDocumentState.IsDirty ? $"{head} — {doc} ●" : $"{head} — {doc}";
    }

    /// <summary>
    /// Live values for the editor status bar (FPS, memory, latency, game
    /// entries). The editor page re-renders on a throttle and reads these
    /// statics.
    /// </summary>
    private static void PublishDiagnostics(float deltaTime)
    {
        UiDiagnostics.Fps = 1f / Math.Max(1e-4f, deltaTime);
        // GC.GetTotalMemory only reports the managed heap and is not a useful
        // measure of the application's footprint. Use the process working set
        // so native/GPU allocations are included as well.
        UiDiagnostics.UsedMemoryBytes = Environment.WorkingSet;
        UiDiagnostics.TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        UiDiagnostics.PingMs = 15f; // demo: no networking yet
        // Status bar entries published by game code through Editor.StatusBar.
        UiDiagnostics.StatusBarEntries = StatusBar.Snapshot();
    }

    /// <summary>Secondary windows host the single-notification popup page.</summary>
    protected override WindowSession CreateExtraSession(IWindow window, IGraphicsDevice? graphics) =>
        new NotificationWindowSession(window, graphics);

    public override void Dispose()
    {
        Current = null;
        GameProject.Dispose();
        base.Dispose();
    }

    private static IFileSystem _backend = null!;
    private static FileSystemService _content = null!;

    /// <summary>
    /// Composes the editor's filesystems: <see cref="FileSystem.Content"/> is the
    /// read-only base content (the output directory's shaders/assets plus the
    /// repo's <c>Editor/Ui</c> mounted at <c>/Ui</c> for hot reload), and
    /// <see cref="FileSystem.Project"/> is the read-write project rooted at the
    /// directory of the <c>.crproj</c> being opened (<paramref name="projectFilePath"/>,
    /// the double-click launch path) — or the repo's <c>Game/</c> directory
    /// (falling back to the copy next to the executable in published builds)
    /// when the editor starts bare.
    /// </summary>
    internal static void ConfigureFileSystem(string? projectFilePath)
    {
        var backend = ZioFileSystem.Physical();

        var probe = new FileSystemService(backend, AppContext.BaseDirectory);
        var gameSource = probe.ResolveSystemDirectory(PathUtil.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Game"));
        var uiSource = probe.ResolveSystemDirectory(PathUtil.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Editor", "Ui"));

        var contentMounts = new Dictionary<FilePath, string>();
        if (uiSource is not null) contentMounts["/Ui"] = uiSource;

        _backend = backend;
        _content = new FileSystemService(new ReadOnlyFileSystem(backend), AppContext.BaseDirectory, contentMounts);
        ApplyProjectRoot(projectFilePath is not null
            ? Path.GetDirectoryName(projectFilePath) ?? AppContext.BaseDirectory
            : gameSource ?? PathUtil.Combine(AppContext.BaseDirectory, "Game"));
    }

    /// <summary>
    /// Re-roots the project filesystem (and nothing else) at
    /// <paramref name="projectRoot"/>. The backend and the read-only content view
    /// are kept as configured at startup; only <see cref="FileSystem.Project"/>
    /// moves, which is exactly the contract of a project switch.
    /// </summary>
    internal static void ApplyProjectRoot(string projectRoot)
    {
        var project = new FileSystemService(_backend, projectRoot);
        FileSystem.Configure(_backend, _content, project);
    }

    /// <summary>
    /// Registers the whole Ui/ folder (routable pages + components, including
    /// the native property dispatcher) on a window's UI runtime, precompiles
    /// the components and navigates to <paramref name="page"/>. Called for the
    /// primary window and for every secondary window: the template
    /// compilation is globally cached (RazorComponentFactory), so registering
    /// per window is cheap and hot reload stays per window.
    /// </summary>
    internal static void ConfigureUiForWindow(UiSystem ui, string page)
    {
        const string uiDirectory = "/Ui";
        var registeredCount = ui.RegisterRazorComponentsFromDirectory(uiDirectory);
        // The property dispatcher is native code (it resolves editors from the
        // [EditorProperty] registry at render time), so it is registered here
        // alongside the .razor components found in the directory.
        ui.RegisterComponent("PropertyEditor", () => new PropertyEditor());
        Log.Info($"Razor UI: registered {registeredCount} file(s) from {uiDirectory}");
        // Parallel precompilation: the first render (Navigate) is only cache
        // hits. Timed to validate the gains.
        var precompileWatch = Stopwatch.StartNew();
        ui.PrecompileAll();
        Log.Info($"Razor UI: precompiled in {precompileWatch.ElapsedMilliseconds} ms");
        var navigateWatch = Stopwatch.StartNew();
        ui.Navigate(page);
        Log.Info($"Razor UI: current page is {ui.CurrentUrl} (navigate {navigateWatch.ElapsedMilliseconds} ms)");
        ui.WatchDirectory(uiDirectory);
    }
}
