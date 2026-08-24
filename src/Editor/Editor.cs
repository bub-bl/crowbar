using System.Diagnostics;
using Crowbar.Engine;
using Crowbar.Engine.InputSystem;
using Crowbar.Engine.Platform;
using Crowbar.Engine.Rendering;
using Crowbar.FileSystems;
using Crowbar.UI;
// The action registry is engine-internal (game code only declares [AssetAction]
// methods): the editor reaches it through InternalsVisibleTo. Aliased so the
// bare name does not collide with the GlobalNamespaces shorthand members.
using AssetActions = Crowbar.Engine.Global.AssetActions;

namespace Crowbar.Editor;

/// <summary>
/// The editor instance: the engine <see cref="Application"/> session that runs
/// the editor. It is the composition root — it owns the startup order, drives
/// the per-frame update phases and the project-switch workflow, and owns the
/// editor tools (<see cref="Project"/>, <see cref="EditorLevel"/>, <see cref="GameProject"/>,
/// <see cref="Viewport"/>, <see cref="Shortcuts"/>, <see cref="NotificationWindow"/>) wired by
/// constructor in <see cref="OnInitialize"/>. It also exposes the live engine
/// state (world, renderer, camera, UI, window, platform) publicly so the shared
/// API (<see cref="GlobalNamespaces.Game"/>) and the tools reach it without a
/// locator. <see cref="Program.Main"/> creates one editor and runs it.
/// </summary>
public sealed class Editor : Application
{
    private readonly string? _startupProjectFile;
    private string _lastWindowTitle = string.Empty;

    public Editor(string? projectFilePath = null)
    {
        _startupProjectFile = projectFilePath;
    }

    /// <summary>The open <c>.crproj</c> project: load, switch, top-bar publish.</summary>
    public Project Project { get; private set; } = null!;

    /// <summary>The open level: load, save, undo/redo, dirty tracking.</summary>
    public EditorLevel Level { get; private set; } = null!;

    /// <summary>The game project (scripts): compile, watch, hot reload → notifications.</summary>
    public GameProject GameProject { get; private set; } = null!;

    /// <summary>The open project's content: panel snapshot + hot-reload watch.</summary>
    public ContentExplorer ContentExplorer { get; private set; } = null!;

    /// <summary>The 3D viewport: gizmos, picking, selection, panel publishing.</summary>
    public Viewport Viewport { get; private set; } = null!;

    /// <summary>The editor's global keyboard shortcuts.</summary>
    public Shortcuts Shortcuts { get; private set; } = null!;

    /// <summary>The persistent notification popup window.</summary>
    public NotificationWindow NotificationWindow { get; private set; } = null!;

    /// <summary>Hot reload for post-process shaders (engine + game project).</summary>
    public ShaderHotReload ShaderHotReload { get; private set; } = null!;

    /// <summary>Consumes and applies generic inspector UI requests.</summary>
    public InspectorBridge InspectorBridge { get; private set; } = null!;

    /// <summary>Handles specialized environment asset imports requested by the content UI.</summary>
    internal EnvironmentAssetImporter EnvironmentAssetImporter { get; private set; } = null!;

    // The engine exposes Platform/OpenWindow/Viewport* as protected session
    // members; the editor tools reach them through these public accessors.
    public new IPlatform Platform => base.Platform;
    public new WindowSession OpenWindow(WindowOptions options) => base.OpenWindow(options);
    public new int ViewportWidth => base.ViewportWidth;
    public new int ViewportHeight => base.ViewportHeight;

    /// <summary>Saves the open level to the project's level file (Ctrl+S).</summary>
    public void SaveLevel() => Level.Save(Project.LevelFileName);

    protected override void OnInitialize()
    {
        // The editor may ship its own [AssetType] resource types: register its
        // assembly like the engine's. Currently a no-op, kept so future editor
        // assets load through the same path. The same registration picks up the
        // editor's own [AssetAction] context actions (ContentActions.Reimport).
        ResourceLibrary.Register(typeof(Editor).Assembly);
        AssetActions.Register(typeof(Editor).Assembly);

        // The editor tools: wired here (the engine session is up), by
        // constructor — a dependency DAG, no locator. The shared Game API was
        // already bound to this session by the Application constructor.
        Project = new Project(this);
        Level = new EditorLevel();
        NotificationWindow = new NotificationWindow(this);
        GameProject = new GameProject(NotificationWindow);
        ContentExplorer = new ContentExplorer();
        // The content snapshot carries each file's declared actions: when the
        // game project (re)loads and its [AssetAction] set changes, republish
        // so the menu shows the fresh actions.
        GameProject.ActionsChanged += ContentExplorer.Refresh;
        Viewport = new Viewport(this);
        InspectorBridge = new InspectorBridge(this);
        EnvironmentAssetImporter = new EnvironmentAssetImporter(this);
        Shortcuts = new Shortcuts(this, NotificationWindow);

        // The translation gizmo snap follows the grid cell size.
        Viewport.ConfigureSnap();

        // The .crproj passed on the command line (double-click launch) is
        // opened first: its directory is the project root the filesystem was
        // configured with, and its name drives the window title and the level
        // file name. A broken file falls back to the bare demo project instead
        // of blocking startup.
        Project.LoadStartup(_startupProjectFile);

        // The content panel snapshot and its hot-reload watch start on the
        // project root the filesystem was configured with.
        ContentExplorer.Start();

        // The game project must start before the level is loaded: only its
        // registered component types resolve when a saved document is
        // materialized below.
        GameProject.Start();

        // Post-process shader hot reload: compiles the project's
        // Content/Shaders on start and watches both shader roots for edits.
        ShaderHotReload = new ShaderHotReload(this, NotificationWindow);
        ShaderHotReload.Start();

        // Persistence: the project's saved level is loaded when it exists (so
        // edits survive a restart), otherwise the demo scene is built from
        // scratch. Either way the open level starts clean — the title bar's
        // "●" appears only once a mutation marks the level dirty (Level.IsDirty).
        var level = Level.Load(Project.LevelFileName);
        World!.Start();
        Log.Info($"[World] {level.Entities.Count} entit(ies) in level '{level.Name}'.");

        // Initial selection: the main cube (or the first mesh in a loaded
        // level), to show the widget.
        Viewport.SelectInitial(level);

        // Editor shortcuts: Ctrl+S saves, Ctrl+Z undoes, Ctrl+Shift+Z (and
        // Ctrl+Y) redo, Ctrl+Shift+N toggles the notification window.
        Shortcuts.Register();
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
        Shortcuts.Update();

        // UI → host requests are applied before the frame's edits, so a click
        // lands immediately on the state the button displayed.
        Level.UpdateRequests(); // undo/redo from the toolbar
        if (EditorProjectState.ConsumeOpenRequest())
            OpenProjectFromDialog(); // modal native dialog, main thread
        if (EditorNotificationWindowState.ConsumeToggleRequest())
            NotificationWindow.Toggle();

        // Live state for the top bar, the custom title bar and the OS title.
        Project.Publish();
        Level.PublishLevelState();
        SyncWindowTitle();

        // Live values for the editor status bar (FPS, memory, latency, game entries).
        PublishDiagnostics(deltaTime);

        // Script host: applies detected hot reloads and prunes the toasts.
        GameProject.Update();

        // Shader hot reload: applies the recompiles the watchers detected.
        ShaderHotReload.Update();

        // Content hot reload: applies the changes the content watcher detected.
        ContentExplorer.Update();

        // Viewport interaction: gizmos, picking, selection, explorer.
        Viewport.Update(deltaTime, ViewportWidth, ViewportHeight);
        // Specialized content workflows run independently from the generic
        // inspector request bridge.
        EnvironmentAssetImporter.Update(Game.Renderer?.Gizmos.Selection);
        // Inspector bridge: consume generic inspector requests and publish state.
        InspectorBridge.Update(Game.Renderer?.Gizmos.Selection);

        Level.PublishUndoState();
        Level.DetectUnbracketedMutations();
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
    /// Hides the cursor only for the editor's viewport mouse-look session.
    /// Other editor windows, including the notification popup, keep their
    /// cursor visible.
    /// </summary>
    protected override bool HideCursorWhileLooking => true;

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

        // Re-point the content panel and its watcher at the new project's assets.
        ContentExplorer.Start();

        // Re-root the post-process shader watcher (project Content/Shaders).
        ShaderHotReload.Start();

        Project.Commit(project, projectFilePath);

        // Reload the game project from the new root first: its component types
        // must be registered before the document is materialized, so saved
        // components resolve when ReloadLevel loads the level.
        GameProject.Start();
        Level.Reload(Project.LevelFileName);
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
        ContentExplorer.Dispose();
        GameProject.Dispose();
        ShaderHotReload.Dispose();
        base.Dispose();
    }

    private static IFileSystem _backend = null!;
    private static FileSystemService _content = null!;

    /// <summary>
    /// Composes the editor's filesystems: <see cref="FileSystem.Content"/> is the
    /// read-only base content (the output directory's shaders/assets plus the
    /// repo's <c>Editor/Ui</c> mounted at <c>/Ui</c> for hot reload and the
    /// open project's <c>Content/</c> folder mounted at <c>/Content</c>), and
    /// <see cref="FileSystem.Project"/> is the read-write project rooted at the
    /// directory of the <c>.crproj</c> being opened (<paramref name="projectFilePath"/>,
    /// the double-click launch path) — or the repo's <c>Game/</c> directory
    /// (falling back to the copy next to the executable in published builds)
    /// when the editor starts bare.
    /// </summary>
    public static void ConfigureFileSystem(string? projectFilePath)
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
    /// Re-roots the project filesystem at <paramref name="projectRoot"/> and
    /// remounts the project's <c>Content/</c> folder into the read-only content
    /// view at <c>/Content</c> (so <c>Content/Models/...</c> paths resolve to
    /// the open project's assets). The backend and the engine built-ins are
    /// kept; only the project view and the <c>/Content</c> mount move, which is
    /// exactly the contract of a project switch.
    /// </summary>
    public static void ApplyProjectRoot(string projectRoot)
    {
        var mounts = new Dictionary<FilePath, string>(_content.Mounts)
        {
            ["/Content"] = Path.Combine(projectRoot, "Content")
        };
        _content = new FileSystemService(new ReadOnlyFileSystem(_backend), _content.ContentRoot, mounts);

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
    public static void ConfigureUiForWindow(UiSystem ui, string page)
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
