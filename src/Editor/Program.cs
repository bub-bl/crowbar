using System.Diagnostics;
using System.Numerics;
using Crowbar.Engine;
using Crowbar.Engine.InputSystem;
using Crowbar.Engine.Rendering;
using Crowbar.Engine.Scripting;
using Crowbar.FileSystems;

using Crowbar.UI;

namespace Crowbar.Editor;

internal static class Program
{
    public static void Main(string[] args)
    {
        var projectFile = ResolveProjectArg(args);
        DemoApplication.ConfigureFileSystem(projectFile);
        new DemoApplication(projectFile).Run();
    }

    /// <summary>
    /// The first command-line argument is the project file to open (the path
    /// Windows passes when a <c>.crproj</c> is double-clicked), or null when
    /// the editor starts bare. Anything else is ignored: it is not a project
    /// file, so the editor falls back to its default demo project.
    /// </summary>
    private static string? ResolveProjectArg(string[] args)
    {
        if (args is not { Length: > 0 } || string.IsNullOrWhiteSpace(args[0]))
            return null;
        var path = Path.GetFullPath(args[0]);
        return path.EndsWith(".crproj", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            ? path
            : null;
    }
}

/// <summary>
/// Demo application: loads the Razor + Yoga + WebGPU showcase page and
/// runs it on the engine's input → update → render loop. The platform, input,
/// UI runtime and graphics device are all owned by <see cref="Application"/>.
/// </summary>
internal sealed class DemoApplication : Application
{
    private ScriptHost? _scriptHost;
    private Level? _demoLevel;
    private string _lastWindowTitle = string.Empty;

    /// <summary>The .crproj currently open (command line or picked in the editor), or null in a bare start.</summary>
    private string? _projectFilePath;

    /// <summary>The open project file (null when the editor starts bare on the demo project).</summary>
    private CrowbarProjectFile? _project;

    internal DemoApplication(string? projectFilePath = null)
    {
        _projectFilePath = projectFilePath;
    }

    /// <summary>Open undo window for the current gizmo drag, committed on release (one step per drag).</summary>
    private IDisposable? _gizmoStep;

    /// <summary>Last observed unbracketed-mutation count of the open document (debug detector).</summary>
    private int _lastUnbracketedMutations;

    /// <summary>
    /// The project-relative path the open level is saved to and loaded from
    /// (Ctrl+S). Named after the project so each project carries its own
    /// level; the bare demo run falls back to "Demo.level".
    /// </summary>
    private string LevelSavePath =>
        _project is { Name.Length: > 0 } project ? $"{project.Name}.level" : "Demo.level";

    protected override void OnInitialize()
    {
        // The translation gizmo snap follows the grid cell size.
        if (Renderer is { } renderer)
            renderer.Gizmos.SnapSize = renderer.Grid.CellSize;

        // The .crproj passed on the command line (double-click launch) is
        // opened first: its directory is the project root the filesystem was
        // configured with, and its name drives the window title and the level
        // file name. A broken file falls back to the bare demo project instead
        // of blocking startup.
        LoadProject();

        // The game project: a real .NET library project (Game/Game.csproj) referencing
        // the engine. It is loaded here at runtime into its own collectible assembly
        // context — the engine and the editor never reference the project — and
        // hot-reloaded on every edit (IL fast path when only method bodies change,
        // otherwise a full reload with state migration). The in-memory compiler is
        // given the same reference set the project declares, so the game project
        // sees the engine API it referenced. The project's Components are
        // registered so the editor can attach them to entities; its code publishes
        // editor status through the engine's Editor.StatusBar API.
        // The game project must start before the level is loaded: only its
        // registered component types resolve when the saved document is
        // materialized below.
        _scriptHost = new ScriptHost(new ScriptCompiler(
        [
            typeof(ScriptHost).Assembly,       // Crowbar.Engine
            typeof(FileSystemService).Assembly, // Crowbar.FileSystem
            typeof(PropertyEditor).Assembly,    // Crowbar.UI
        ]));
        _scriptHost.Reloaded += OnScriptReloaded;
        _scriptHost.ReloadFailed += OnScriptReloadFailed;
        StartGameProject();

        // Persistence: the project's saved level is loaded when it exists (so
        // edits survive a restart), otherwise the demo scene is built from
        // scratch. Either way the open document starts clean — the title bar's
        // "●" appears only once a mutation marks the level dirty (Level.IsDirty).
        _demoLevel = LoadOrCreateLevel();
        _demoLevel.ClearDirty();

        World.Start();
        Console.WriteLine($"World: {_demoLevel.Entities.Count} entit(ies) in level '{_demoLevel.Name}'.");

        // Initial selection: the main cube (or the first mesh in a loaded
        // level), to show the widget.
        Renderer?.Gizmos.Selection = SelectInitialEntity(_demoLevel);

        // Editor shortcuts: Ctrl+S saves, Ctrl+Z undoes, Ctrl+Shift+Z (and
        // Ctrl+Y) redo. Shortcuts never fire while a text field owns the
        // keyboard (the UI consumes it then), so a field's own editing is
        // never hijacked. The keys are resolved by the character they produce
        // in the active layout (like the camera's ZQSD bindings): Ctrl+Z
        // always means the key labeled Z, whether the layout is AZERTY or
        // QWERTY (the Key enum itself is scancode-based, i.e. physical).
        ShortcutManager.Instance.IsEnabled = () => !Ui.KeyboardConsumed;
        var undoKey = InputSource.KeyForChar('z');
        var redoKey = InputSource.KeyForChar('y');
        ShortcutManager.Instance.Register(new KeyChord(Key.S, KeyModifiers.Control), SaveLevel);
        ShortcutManager.Instance.Register(new KeyChord(undoKey, KeyModifiers.Control), Undo);
        ShortcutManager.Instance.Register(new KeyChord(undoKey, KeyModifiers.Control | KeyModifiers.Shift), Redo);
        ShortcutManager.Instance.Register(new KeyChord(redoKey, KeyModifiers.Control), Redo);

        // Publish the real world hierarchy for the Explorer panel: the page
        // reads it through EditorExplorerState on every render. The inspector
        // reads the same selection through EditorInspectorState.
        ExplorerTreeBuilder.Publish(World, Renderer?.Gizmos.Selection);
        InspectorStateBuilder.Publish(Renderer?.Gizmos.Selection);

        // Automatic registration of the whole Ui/ folder: files with @page
        // become routable pages, the others become components.
        const string uiDirectory = "/Ui";
        var registeredCount = Ui.RegisterRazorComponentsFromDirectory(uiDirectory);
        // The property dispatcher is native code (it resolves editors from the
        // [EditorProperty] registry at render time), so it is registered here
        // alongside the .razor components found in the directory.
        Ui.RegisterComponent("PropertyEditor", () => new PropertyEditor());
        Console.WriteLine($"Razor UI: registered {registeredCount} file(s) from {uiDirectory}");
        // Parallel precompilation: the first render (Navigate) is only cache
        // hits. Timed to validate the gains.
        var precompileWatch = Stopwatch.StartNew();
        Ui.PrecompileAll();
        Console.WriteLine($"Razor UI: precompiled in {precompileWatch.ElapsedMilliseconds} ms");
        var navigateWatch = Stopwatch.StartNew();
        Ui.Navigate("/editor");
        Console.WriteLine($"Razor UI: current page is {Ui.CurrentUrl} (navigate {navigateWatch.ElapsedMilliseconds} ms)");
        Ui.WatchDirectory(uiDirectory);
    }

    /// <summary>
    /// (Re)loads the game project from the current project root: compiles every
    /// *.cs file under <see cref="FileSystem.Project"/>'s root into the
    /// collectible assembly context, registers its component types so the editor
    /// can attach them, and watches the directory for hot reload. Calling it
    /// again (project switch) compiles the new project from scratch.
    /// </summary>
    private void StartGameProject()
    {
        try
        {
            // The game project is the whole project (FileSystem.Project): "." is its root.
            const string gameDirectory = ".";
            // The previous generation's component types are dropped (a project
            // switch or full reload compiles a new assembly), then the fresh
            // project's components are registered so they become attachable.
            // The project's own code publishes editor status through
            // Editor.StatusBar; clear a previous project's entries so only the
            // live project's registrations survive.
            var previous = _scriptHost!.Current;
            _scriptHost.WatchDirectory(gameDirectory, "GameProject");
            if (previous is not null)
                ComponentTypeRegistry.UnregisterAssembly(previous.Assembly);
            if (_scriptHost.Current is { } current)
                ComponentTypeRegistry.RegisterAssembly(current.Assembly);
            StatusBar.Clear();
            Console.WriteLine($"[Scripting] Game project loaded: {_scriptHost.Current?.TypesByFullName.Count ?? 0} type(s) from the project");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scripting] Failed to load the game project: {ex.Message}");
            UiNotifications.Show("Script", "Failed to load the game project", "error");
        }
    }

    /// <summary>
    /// Opens the <c>.crproj</c> given on the command line (double-click launch).
    /// Its directory was already chosen as the project filesystem root by
    /// <see cref="ConfigureFileSystem"/>, so it loads through the project
    /// filesystem by its file name. A missing or unreadable file leaves the
    /// editor on the bare demo project and surfaces an error notification.
    /// </summary>
    private void LoadProject()
    {
        if (_projectFilePath is null)
            return;

        try
        {
            _project = CrowbarProjectFile.Load(Path.GetFileName(_projectFilePath));
            Console.WriteLine($"[Project] Opened '{_projectFilePath}': {_project.Name} v{_project.Version}.");
            PublishProjectState();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Project] Failed to load '{_projectFilePath}': {ex.Message}");
            UiNotifications.Show("Project", $"Unreadable project: {Path.GetFileName(_projectFilePath)}", "error");
        }
    }

    /// <summary>
    /// Publishes the open project to the top bar (name + file path) so it can
    /// display the current project and hash it for re-render.
    /// </summary>
    private void PublishProjectState() =>
        EditorProjectState.Publish(_project?.Name ?? string.Empty, _projectFilePath ?? string.Empty);

    /// <summary>
    /// Opens a native Explorer dialog to pick a <c>.crproj</c> file, then
    /// switches the editor to that project (filesystem root, document, game project).
    /// </summary>
    private void OpenProjectFromDialog()
    {
        string? initialDirectory = null;
        if (_projectFilePath is not null)
            initialDirectory = Path.GetDirectoryName(_projectFilePath);
        if (string.IsNullOrEmpty(initialDirectory))
            initialDirectory = FileSystem.Project.ContentRoot;

        var path = NativeFileDialog.PickCrproj(Window.NativeHandle, initialDirectory);
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
        CrowbarProjectFile project;
        try
        {
            project = CrowbarProjectFile.LoadFromDisk(projectFilePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Project] Failed to open '{projectFilePath}': {ex.Message}");
            UiNotifications.Show("Project", $"Unreadable project: {Path.GetFileName(projectFilePath)}", "error");
            return;
        }

        var projectRoot = Path.GetDirectoryName(projectFilePath);
        if (string.IsNullOrEmpty(projectRoot))
            return;

        // Stop play mode before tearing the document down.
        World.Stop();

        // Re-root the project filesystem at the new project's directory. Levels,
        // game project scripts and every save resolve through FileSystem.Project,
        // so this one call redirects all subsequent reads/writes.
        ApplyProjectRoot(projectRoot);

        _project = project;
        _projectFilePath = projectFilePath;

        // Reload the game project from the new root first: its component types must
        // be registered before the document is materialized, so saved
        // components resolve when ReloadDocument loads the level.
        StartGameProject();
        ReloadDocument();
        PublishProjectState();

        Console.WriteLine($"[Project] Project opened: {project.Name} v{project.Version} from {projectFilePath}");
        UiNotifications.Show("Project", $"Project opened: {project.Name}", "success");
    }

    /// <summary>
    /// Destroys the current document (level) and loads the one from the project's
    /// root, then re-publishes the hierarchy, inspector, selection and undo state
    /// so the panels reflect the new document.
    /// </summary>
    private void ReloadDocument()
    {
        _gizmoStep?.Dispose();
        _gizmoStep = null;

        if (_demoLevel is { } old)
        {
            World.DestroyLevel(old);
            _demoLevel = null;
        }

        Renderer?.Gizmos.Selection = null;
        _demoLevel = LoadOrCreateLevel();
        _demoLevel.ClearDirty();

        ExplorerTreeBuilder.Publish(World, null);
        InspectorStateBuilder.Publish(null);
        EditorUndoState.Publish(false, false, null, null);
        _lastUnbracketedMutations = 0;
    }

    /// <summary>
    /// The open level: the project's <see cref="LevelSavePath"/> when it was
    /// saved before, otherwise the freshly built demo scene. A saved file that
    /// fails to parse or references removed content falls back to the demo
    /// scene instead of blocking startup.
    /// </summary>
    private Level LoadOrCreateLevel()
    {
        if (FileSystem.Project.FileExists(LevelSavePath))
        {
            try
            {
                var file = LevelFile.Load(LevelSavePath);
                var level = file.CreateLevel(World);
                Console.WriteLine($"[Level] Loaded '{LevelSavePath}': {level.Entities.Count} entit(ies).");
                return level;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Level] Failed to load '{LevelSavePath}': {ex.Message} — building the demo scene.");
                UiNotifications.Show("Level", $"Level '{LevelSavePath}' unreadable — demo scene loaded", "error");
            }
        }

        return BuildDemoLevel();
    }

    /// <summary>
    /// Builds the demo scene: a level with a directional light (key), a point
    /// light (fill), a floor plane and several cubes/models rendered by
    /// MeshRenderers through the world system (World).
    /// </summary>
    private Level BuildDemoLevel()
    {
        var level = World.CreateLevel("Demo");

        var sun = level.SpawnEntity("Sun");
        var sunLight = sun.AddComponent<DirectionalLight>();
        sunLight.Color = new Vector3(1f, 0.95f, 0.85f);
        sunLight.Intensity = 1.6f;
        sunLight.CastShadows = true;
        sunLight.Local = new Transform(
            Vector3.Zero,
            Rotation.FromYaw(-45f) * Rotation.FromPitch(-35f),
            Vector3.One);

        var fill = level.SpawnEntity("FillLight");
        var fillLight = fill.AddComponent<PointLight>();
        fillLight.Color = new Vector3(0.4f, 0.6f, 1f);
        fillLight.Intensity = 4f;
        fillLight.Range = 8f;
        fillLight.Local = new Transform(
            new Vector3(-2.5f, 2f, -1.5f),
            Rotation.Identity,
            Vector3.One);

        // The floor: a plane primitive lying on the XZ grid plane, at the
        // origin (0, 0, 0). Scaled to 5×5 so it forms a floor under the
        // cubes around it.
        var plane = level.SpawnEntity("Plane");
        var planeMesh = plane.AddComponent<MeshRenderer>();
        planeMesh.Model = Model.CreatePlane();
        planeMesh.Material = Material.FromShader("Surface/StandardPbr")
            .Set("color", new Vector4(0.3f, 0.33f, 0.3f, 1f))
            .Set("metallic", 0f)
            .Set("roughness", 0.85f)
            .Set("occlusion", 1f)
            .Set("emissive", 0f);
        planeMesh.Local = new Transform(
            Vector3.Zero,
            Rotation.Identity,
            new Vector3(5f, 1f, 5f));

        var cube = level.SpawnEntity("Cube");
        var mesh = cube.AddComponent<MeshRenderer>();
        mesh.Model = Model.CreateCube();
        mesh.Material = Material.FromShader("Surface/StandardPbr")
            .Set("color", new Vector4(0.2f, 0.6f, 1.0f, 1.0f))
            .Set("metallic", 0.15f)
            .Set("roughness", 0.45f)
            .Set("occlusion", 1f)
            .Set("emissive", 0f);
        // Sits on the plane: the unit cube's bottom face is at y = 0.
        mesh.Local = new Transform(
            new Vector3(0f, 0.5f, 0f),
            Rotation.FromYaw(30f) * Rotation.FromPitch(15f),
            Vector3.One);

        // A second, smaller cube with the Unlit shader (3 lines thanks to the
        // includes): proves the shader API is reusable.
        var accent = level.SpawnEntity("Accent");
        var accentMesh = accent.AddComponent<MeshRenderer>();
        accentMesh.Model = Model.CreateCube();
        accentMesh.Material = Material.FromShader("Surface/Unlit")
            .Set("color", new Vector4(1f, 0.72f, 0.08f, 1f));
        // Half the cube's height (0.25) above the floor: it rests on the plane.
        accentMesh.Local = new Transform(
            new Vector3(2.2f, 0.25f, 1.4f),
            Rotation.FromYaw(-20f) * Rotation.FromPitch(10f),
            new Vector3(0.5f));

        // A last cube without a material: it picks up the default material
        // (Surface/Standard.slang), the renderer's fallback path.
        var fallback = level.SpawnEntity("DefaultCube");
        var fallbackMesh = fallback.AddComponent<MeshRenderer>();
        fallbackMesh.Model = Model.CreateCube();
        fallbackMesh.Local = new Transform(
            new Vector3(-2.2f, 0.25f, 1.4f),
            Rotation.FromYaw(-20f) * Rotation.FromPitch(10f),
            new Vector3(0.5f));

        // A glTF model imported through Model.Load: Assimp converts its
        // geometry and the engine turns the glTF PBR material into a
        // Surface/StandardPbr material with its albedo + metallic-roughness
        // textures bound. The mesh carries its own material, so no renderer
        // override is needed.
        var crate = level.SpawnEntity("Crate");
        var crateMesh = crate.AddComponent<MeshRenderer>();
        try
        {
            crateMesh.Model = Model.Load("Assets/Models/Crate/Crate.gltf");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Crate model failed to load: {ex.Message}");
            crateMesh.Model = Model.Error;
        }
        crateMesh.Local = new Transform(
            new Vector3(1.6f, 0.5f, -1.6f),
            Rotation.FromYaw(35f),
            Vector3.One);

        // The real-world Sketchfab test: a multi-mesh glTF with a transparent
        // bulb and a tripod. Its geometry lives in scene.bin next to the .gltf;
        // when that buffer is present the loader bakes the node transforms
        // (PreTransformVertices) and converts both PBR materials. Until then it
        // reports the missing file and falls back to the error model.
        var workLight = level.SpawnEntity("IndustrialWorkLight");
        var workLightMesh = workLight.AddComponent<MeshRenderer>();
        Model workLightModel;
        try
        {
            workLightModel = Model.Load("Assets/Models/industrial_work_light/industrial_work_light.gltf");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Model] industrial_work_light: {ex.Message}");
            workLightModel = Model.Error;
        }
        workLightMesh.Model = workLightModel;

        // Sketchfab models arrive with arbitrary extents; normalize the bounds
        // to roughly two units so a real asset fits the demo scene.
        var workLightExtent = workLightModel.Bounds.Max - workLightModel.Bounds.Min;
        var workLightMaxExtent = MathF.Max(workLightExtent.X, MathF.Max(workLightExtent.Y, workLightExtent.Z));
        var workLightScale = workLightMaxExtent > 0.001f ? 2f / workLightMaxExtent : 1f;
        workLightMesh.Local = new Transform(
            new Vector3(-1.6f, 0.5f, -1.6f),
            Rotation.FromYaw(-25f),
            new Vector3(workLightScale));

        return level;
    }

    /// <summary>The entity selected at startup: the main cube, else the first mesh, else the first entity.</summary>
    private static Entity? SelectInitialEntity(Level level) =>
        level.Entities.FirstOrDefault(e => e.Name == "Cube")
        ?? level.Entities.FirstOrDefault(e => e.GetComponent<MeshRenderer>() is not null)
        ?? level.Entities.FirstOrDefault();

    /// <summary>
    /// Saves the open level to <see cref="LevelSavePath"/> (Ctrl+S) and clears
    /// the dirty flag. Failures surface as an error notification and keep the
    /// document dirty, so nothing is silently lost.
    /// </summary>
    private void SaveLevel()
    {
        if (_demoLevel is not { IsValid: true })
            return;

        try
        {
            LevelFile.Save(_demoLevel, LevelSavePath);
            _demoLevel.ClearDirty();
            Console.WriteLine($"[Level] Saved: {LevelSavePath}");
            UiNotifications.Show("Level", $"Level saved: {LevelSavePath}", "success");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Level] Save failed: {ex}");
            UiNotifications.Show("Level", $"Save failed: {ex.Message}", "error");
        }
    }

    /// <summary>Undoes the last edit of the open document (Ctrl+Z or the toolbar button).</summary>
    private void Undo()
    {
        _demoLevel?.History.Undo();
        // Undo mid-drag commits the drag window itself: reset the host's handle
        // so the rest of the gesture opens a fresh window (the disposed one is
        // a no-op anyway, but it would block a new window for the next frame).
        _gizmoStep = null;
        RefreshSelectionAfterRestore();
    }

    /// <summary>Redoes the last undone edit (Ctrl+Shift+Z, Ctrl+Y or the toolbar button).</summary>
    private void Redo()
    {
        _demoLevel?.History.Redo();
        _gizmoStep = null;
        RefreshSelectionAfterRestore();
    }

    /// <summary>
    /// Undo/redo restores the document by rebuilding entities: the gizmo's
    /// selection reference points at the destroyed generation. Re-resolve it by
    /// its stable id (the serializer preserves ids), so the selection survives
    /// an undo/redo and the inspector/explorer keep showing the same entity.
    /// </summary>
    private void RefreshSelectionAfterRestore()
    {
        if (Renderer?.Gizmos.Selection is not { } selected)
            return;
        Renderer.Gizmos.Selection = World.FindEntity(selected.Id);
    }

    /// <summary>
    /// Publishes the undo/redo capability of the open document for the toolbar
    /// (buttons enabled/disabled and step labels in their tooltips). The panels
    /// read it through the static bridge; the history itself stays on the
    /// document (<c>Level.History</c>), so multi-window/multi-document hosts
    /// publish each document's own state through the same bridge.
    /// </summary>
    private void PublishUndoState()
    {
        if (_demoLevel is not { } document)
        {
            EditorUndoState.Publish(false, false, null, null);
            return;
        }

        var history = document.History;
        EditorUndoState.Publish(history.CanUndo, history.CanRedo, history.UndoLabel, history.RedoLabel);
    }

    /// <summary>
    /// The undo "no miss" safety net: asserts when a document mutation was
    /// observed outside any undo window since the last frame. Because the
    /// history counts unbracketed mutations at the moment they occur, a window
    /// opened and closed within this very frame (an inspector edit) is still
    /// recognized as bracketed. Runtime script mutations during play are
    /// expected and excluded; in edit mode an unbracketed mutation is a missed
    /// <c>Step()</c> and fails loudly instead of silently corrupting undo.
    /// </summary>
    private void DetectUnbracketedMutations()
    {
        // A disposed document (world teardown) can no longer receive user edits.
        if (_demoLevel is not { IsValid: true } document)
            return;

        var history = document.History;
        if (history.UnbracketedMutationCount == _lastUnbracketedMutations)
            return;
        _lastUnbracketedMutations = history.UnbracketedMutationCount;

        if (World.IsPlaying)
            return;
        Debug.Assert(false, "[Undo] A document mutation escaped its undo window — wrap the action in level.History.Step().");
        UiNotifications.Show("Undo", "Mutation outside an undo window — add a Step().", "error");
    }

    /// <summary>The undo step label of a gizmo drag, by tool.</summary>
    private static string DragLabel(GizmoMode mode) => mode switch
    {
        GizmoMode.Rotate => "Rotate",
        GizmoMode.Scale => "Resize",
        _ => "Move"
    };

    /// <summary>
    /// Drives the viewport gizmos: left-click selects the entity under the
    /// cursor (CPU ray against the mesh renderers' AABBs), then dragging a
    /// widget axis moves the entity along that axis with grid snapping. The
    /// right button keeps its camera-orbit role.
    /// </summary>
    protected override void OnUpdate(float deltaTime)
    {
        base.OnUpdate(deltaTime);

        // Fire the registered editor shortcuts (Ctrl+S saves, Ctrl+Z undoes,
        // Ctrl+Shift+Z/Ctrl+Y redo); Input.Poll already ran this frame, so the
        // press edges are fresh.
        ShortcutManager.Instance.Update();

        // Undo/redo requested by the toolbar buttons (UI → host) are applied
        // before the frame's edits, so a click lands immediately on the state
        // the button displayed.
        if (_demoLevel is not null)
        {
            if (EditorUndoState.ConsumeUndoRequest())
                Undo();
            if (EditorUndoState.ConsumeRedoRequest())
                Redo();
        }

        // Project open requested by the top bar (UI → host): the native dialog
        // is modal, so it runs here on the main thread before the frame's edits.
        if (EditorProjectState.ConsumeOpenRequest())
            OpenProjectFromDialog();

        // The open project shown in the top bar and the OS title (project +
        // document). Published every frame; the bridges no-op when unchanged.
        PublishProjectState();

        // Document shown in the custom title bar: the open level with its real
        // dirty state (unsaved edits → "●"). The OS title follows the same
        // document, so Alt-Tab shows the open level too.
        EditorDocumentState.Publish(_demoLevel?.Name ?? string.Empty, _demoLevel?.IsDirty ?? false);
        SyncWindowTitle();

        // Live values for the editor status bar (FPS, memory, latency). The
        // editor page re-renders on a throttle and reads these statics.
        UiDiagnostics.Fps = 1f / Math.Max(1e-4f, deltaTime);
        // GC.GetTotalMemory only reports the managed heap and is not a useful
        // measure of the application's footprint. Use the process working set
        // so native/GPU allocations are included as well.
        UiDiagnostics.UsedMemoryBytes = Environment.WorkingSet;
        UiDiagnostics.TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        UiDiagnostics.PingMs = 15f; // demo: no networking yet

        // Script host: applies detected hot reloads and prunes the toasts.
        _scriptHost?.Update();
        UiNotifications.PruneExpired();
        // Status bar entries published by game code through Editor.StatusBar
        // (game components register their own; the editor just renders them).
        UiDiagnostics.StatusBarEntries = StatusBar.Snapshot();

        // Selection requested from the Explorer (UI → host): applied to the
        // viewport gizmos, then the real hierarchy is republished so the panel
        // reflects the current state (levels, entities, attachments).
        if (EditorExplorerState.ConsumeRequestedSelection() is { } requestedId &&
            World.FindEntity(requestedId) is { } requested)
            Renderer?.Gizmos.Selection = requested;

        // Edits queued by the inspector (UI → host) are written back to the
        // selected entity inside one undo window: the whole batch (a field
        // commit) is a single undoable step. A successful edit marks the level
        // dirty through Level.MarkDirty (inside ApplyEdit).
        var selected = Renderer?.Gizmos.Selection;
        var pendingEdits = EditorInspectorState.ConsumeEdits();
        if (selected is not null && pendingEdits.Count > 0)
        {
            using var step = _demoLevel!.History.Step("Edit a property");
            foreach (var (key, value) in pendingEdits)
                InspectorStateBuilder.ApplyEdit(selected, key, value);
        }

        // The inspector's Add Component menu offers every component the selected
        // entity can still attach (engine + game project); the requests it
        // queues are applied here inside one undoable step (attaching marks the
        // level dirty), then the inspector is republished so the new
        // component's section appears immediately.
        var addRequests = EditorInspectorState.ConsumeAddComponentRequests();
        if (selected is not null && addRequests.Count > 0)
        {
            using var step = _demoLevel!.History.Step("Add a component");
            foreach (var typeName in addRequests)
                AddComponent(selected, typeName);
        }
        EditorInspectorState.PublishAvailableComponents(AttachableComponentTypes(selected));

        ExplorerTreeBuilder.Publish(World, selected);
        InspectorStateBuilder.Publish(selected);

        var renderer = Renderer;
        if (renderer is null)
        {
            PublishUndoState();
            DetectUnbracketedMutations();
            return;
        }

        // The gizmo tool chosen in the viewport toolbar is applied here every
        // frame: rendering and interaction follow the same mode.
        renderer.Gizmos.Mode = GizmoToolState.Mode switch
        {
            1 => GizmoMode.Rotate,
            2 => GizmoMode.Scale,
            _ => GizmoMode.Translate
        };

        // The 3D scene is rendered in the docked viewport (not the whole
        // window): the DockArea publishes its rectangle through
        // Ui.SceneViewport, and we hand it to the renderer, which adjusts the
        // camera aspect and clips the scene to that rectangle. Before the
        // first layout, it falls back to the whole window.
        var viewport = Ui.SceneViewport;
        renderer.SetSceneViewport(viewport);

        var rect = viewport ?? new UiRect(0, 0, ViewportWidth, ViewportHeight);
        var width = Math.Max(1, (int)rect.Width);
        var height = Math.Max(1, (int)rect.Height);
        var mouse = Mouse.Position;
        var localMouse = mouse - new Vector2(rect.X, rect.Y);
        var insideViewport = IsPointerInsideViewport();

        // A click consumed by the UI (button, tab, input, scrollbar) must
        // neither start a gizmo drag nor select the scene underneath. A drag
        // already engaged continues even if the cursor moves over the UI.
        var matrices = CameraMatrices.Compute(Camera, width, height);
        if (!Ui.PointerPressConsumed || renderer.Gizmos.IsDragging)
            renderer.Gizmos.UpdateInteraction(matrices, localMouse, Mouse.IsDown(MouseButton.Left));

        // One undo window per drag gesture: opened when the drag starts (BeginDrag
        // does not write — the first Drag write happens on the next frame, inside
        // the window) and committed on release, so hundreds of intermediate
        // transform writes become a single undoable step.
        var gizmos = renderer.Gizmos;
        if (gizmos.IsDragging && _gizmoStep is null)
            _gizmoStep = _demoLevel!.History.Step(DragLabel(gizmos.Mode));
        else if (!gizmos.IsDragging && _gizmoStep is not null)
        {
            _gizmoStep.Dispose();
            _gizmoStep = null;
        }

        // Selects on left-click only when the click did not start a gizmo drag
        // (otherwise moving the entity would re-select the scene), only inside
        // the viewport, and not when the UI consumed the click.
        if (insideViewport && !Ui.PointerPressConsumed && Mouse.WasPressed(MouseButton.Left) && !renderer.Gizmos.IsDragging)
            renderer.Gizmos.Selection = renderer.Gizmos.Pick(World, matrices, localMouse);

        PublishUndoState();
        DetectUnbracketedMutations();
    }

    /// <summary>
    /// The camera orbit only starts when the right press begins inside the
    /// viewport and is not consumed by the UI (toolbar button, tab, input,
    /// scrollbar, overlay). The decision is made at press time: a drag
    /// engaged in the viewport continues even if the cursor moves over a
    /// panel, and a press on the UI never orbits.
    /// </summary>
    protected override bool CanMouseLook()
    {
        if (Ui.PointerPressConsumed)
            return false;
        return IsPointerInsideViewport();
    }

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
        IsPointerInsideViewport() && !Ui.WheelConsumed;

    /// <summary>
    /// Pan (middle mouse) only starts inside the viewport and off the UI,
    /// like the orbit: a press on a panel never moves the scene.
    /// </summary>
    protected override bool CanPan() =>
        IsPointerInsideViewport() && !Ui.PointerPressConsumed;

    /// <summary>
    /// While orbiting, the mouse stays confined inside the viewport: even
    /// hidden, it must not slide over the neighbouring panels (it would
    /// reappear off-scene on release). The orbit delta stays unlimited
    /// (measured before the confinement): pushing against an edge keeps
    /// rotating, only the cursor is held inside the scene.
    /// </summary>
    protected override UiRect? MouseLookClampRect =>
        Ui.SceneViewport ?? new UiRect(0, 0, ViewportWidth, ViewportHeight);

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
        var head = _project is { Name.Length: > 0 } project ? $"Crowbar — {project.Name}" : $"Crowbar — {Ui.CurrentUrl}";
        var doc = EditorDocumentState.Title;
        if (doc.Length == 0) return head;
        return EditorDocumentState.IsDirty ? $"{head} — {doc} ●" : $"{head} — {doc}";
    }

    /// <summary>True when the cursor is inside the docked viewport rectangle (the whole window before the first layout).</summary>
    private bool IsPointerInsideViewport()
    {
        var rect = Ui.SceneViewport ?? new UiRect(0, 0, ViewportWidth, ViewportHeight);
        var mouse = Mouse.Position;
        return mouse.X >= rect.X && mouse.X <= rect.Right &&
               mouse.Y >= rect.Y && mouse.Y <= rect.Bottom;
    }

    private void OnScriptReloaded(ScriptReloadedEventArgs e)
    {
        var detail = e.Mode switch
        {
            ScriptReloadMode.FastPath => $"IL fast path: {e.PatchedMethods} method(s) patched in {e.Duration.TotalMilliseconds:0} ms",
            ScriptReloadMode.FullReload => $"Full reload: {e.UpgradedInstances} instance(s) migrated in {e.Duration.TotalMilliseconds:0} ms",
            _ => $"Loaded in {e.Duration.TotalMilliseconds:0} ms"
        };
        Console.WriteLine($"[Scripting] Hot reload OK ({e.Mode}): {detail}");
        UiNotifications.Show("Hot reload", detail, "success");

        // A full reload swaps the live assembly: point the registered game
        // components at the new generation, so the Add Component list offers
        // the reloaded types (not the unloaded ones). The IL fast path keeps
        // the live assembly unchanged, so the types stay valid.
        if (e.Mode == ScriptReloadMode.FullReload)
        {
            ComponentTypeRegistry.UnregisterAssembly(e.Previous.Assembly);
            ComponentTypeRegistry.RegisterAssembly(e.Current.Assembly);
        }
    }

    private static void OnScriptReloadFailed(ScriptReloadFailedEventArgs e)
    {
        Console.WriteLine($"[Scripting] Hot reload FAILED: {e.Error.Message}");
        UiNotifications.Show("Hot reload", $"Failed: {e.Error.Message}", "error");
    }

    /// <summary>
    /// The component types the selected entity can still attach: every concrete
    /// instantiable component type (engine + registered game project), minus the
    /// ones already on the entity. The inspector's Add Component menu offers them.
    /// </summary>
    private static IReadOnlyList<string> AttachableComponentTypes(Entity? entity)
    {
        if (entity is null)
            return [];
        return ComponentTypeRegistry.AllComponentTypes
            .Where(type => entity.GetComponent(type) is null)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .Select(type => type.Name)
            .ToArray();
    }

    /// <summary>
    /// Attaches the named component to the entity through the registry, so the
    /// editor never references game types (it resolves them by name). A malformed
    /// name, an already-present type or a throwing constructor is ignored.
    /// </summary>
    private static void AddComponent(Entity? entity, string typeName)
    {
        if (entity is null || string.IsNullOrEmpty(typeName))
            return;
        var type = ComponentTypeRegistry.Resolve(typeName);
        if (type is null || entity.GetComponent(type) is not null)
            return;
        try
        {
            if (Activator.CreateInstance(type) is Component component)
                entity.AddComponent(component);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Inspector] Failed to add component '{typeName}': {ex.Message}");
        }
    }

    private int ViewportWidth =>
        Math.Max(1, Window.FramebufferWidth > 0 ? Window.FramebufferWidth : Window.Width);

    private int ViewportHeight =>
        Math.Max(1, Window.FramebufferHeight > 0 ? Window.FramebufferHeight : Window.Height);

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
}
