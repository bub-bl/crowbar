using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Crowbar.Engine;
using Crowbar.Engine.InputSystem;
using Crowbar.Engine.Rendering;
using Crowbar.Engine.Scripting;
using Crowbar.FileSystems;

using Crowbar.UI;

namespace Crowbar.Editor;

internal static class Program
{
    public static void Main()
    {
        DemoApplication.ConfigureFileSystem();
        new DemoApplication().Run();
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
    private readonly object? _gamemodeHolder = new GamemodeHolder();
    private MethodInfo? _describe;
    private Level? _demoLevel;
    private string _lastWindowTitle = string.Empty;

    protected override void OnInitialize()
    {
        // The translation gizmo snap follows the grid cell size.
        if (Renderer is { } renderer)
            renderer.Gizmos.SnapSize = renderer.Grid.CellSize;

        // Demo scene: a level with a directional light (key), a point light
        // (fill) and two cubes rendered by MeshRenderers through the world
        // system (World).
        var level = World.CreateLevel("Demo");
        _demoLevel = level;

        var sun = level.SpawnEntity("Sun");
        var sunLight = sun.AddComponent<DirectionalLight>();
        sunLight.Color = new Vector3(1f, 0.95f, 0.85f);
        sunLight.Intensity = 1.6f;
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

        var cube = level.SpawnEntity("Cube");
        var mesh = cube.AddComponent<MeshRenderer>();
        mesh.Model = Model.CreateCube();
        mesh.Material = Material.FromShader("Pbr")
            .Set("color", new Vector4(0.2f, 0.6f, 1.0f, 1.0f))
            .Set("metallic", 0.15f)
            .Set("roughness", 0.45f)
            .Set("occlusion", 1f)
            .Set("emissive", 0f);
        mesh.Local = new Transform(
            Vector3.Zero,
            Rotation.FromYaw(30f) * Rotation.FromPitch(15f),
            Vector3.One);

        // A second, smaller cube with the Unlit shader (3 lines thanks to the
        // includes): proves the shader API is reusable.
        var accent = level.SpawnEntity("Accent");
        var accentMesh = accent.AddComponent<MeshRenderer>();
        accentMesh.Model = Model.CreateCube();
        accentMesh.Material = Material.FromShader("Unlit")
            .Set("color", new Vector4(1f, 0.72f, 0.08f, 1f));
        accentMesh.Local = new Transform(
            new Vector3(2.2f, 0.9f, 1.4f),
            Rotation.FromYaw(-20f) * Rotation.FromPitch(10f),
            new Vector3(0.5f));

        // A last cube without a material: it picks up the default material
        // (Mesh.wgsl), the renderer's fallback path.
        var fallback = level.SpawnEntity("DefaultCube");
        var fallbackMesh = fallback.AddComponent<MeshRenderer>();
        fallbackMesh.Model = Model.CreateCube();
        fallbackMesh.Local = new Transform(
            new Vector3(-2.2f, 0.9f, 1.4f),
            Rotation.FromYaw(-20f) * Rotation.FromPitch(10f),
            new Vector3(0.5f));

        World.Start();
        Console.WriteLine($"World: {level.Entities.Count} entité(s) dans le level '{level.Name}'.");

        // Initial selection: the main cube, to show the widget.
        Renderer?.Gizmos.Selection = cube;

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

        // Demo gamemode: the Game/ folder is compiled by a ScriptHost and
        // hot-reloaded on every edit (IL fast path when only method bodies
        // change, otherwise a full reload with state migration).
        _scriptHost = new ScriptHost();
        _scriptHost.Reloaded += OnScriptReloaded;
        _scriptHost.ReloadFailed += OnScriptReloadFailed;
        try
        {
            // The gamemode is the whole project (FileSystem.Project): "." is its root.
            const string gameDirectory = ".";
            _scriptHost.WatchDirectory(gameDirectory, "DemoGamemode");
            ((GamemodeHolder)_gamemodeHolder!).Current = _scriptHost.Current!.CreateInstance("Game.DemoGamemode");
            _scriptHost.WatchInstance(_gamemodeHolder!);
            ResolveDescribe();
            Console.WriteLine($"[Scripting] Gamemode chargé : {_scriptHost.Current!.TypesByFullName.Count} type(s) depuis le projet gamemode");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scripting] Initial script load failed: {ex.Message}");
            UiNotifications.Show("Script", "Échec du chargement initial du gamemode", "error");
        }
    }

    /// <summary>
    /// Drives the viewport gizmos: left-click selects the entity under the
    /// cursor (CPU ray against the mesh renderers' AABBs), then dragging a
    /// widget axis moves the entity along that axis with grid snapping. The
    /// right button keeps its camera-orbit role.
    /// </summary>
    protected override void OnUpdate(float deltaTime)
    {
        base.OnUpdate(deltaTime);

        // Document shown in the custom title bar (the open level; the dirty
        // flag will come from the asset/save system). The OS title follows the
        // same document, so Alt-Tab shows the open level too.
        EditorDocumentState.Publish(_demoLevel?.Name ?? string.Empty, isDirty: false);
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
        UiDiagnostics.ScriptStatus = DescribeGamemode();

        // Selection requested from the Explorer (UI → host): applied to the
        // viewport gizmos, then the real hierarchy is republished so the panel
        // reflects the current state (levels, entities, attachments).
        if (EditorExplorerState.ConsumeRequestedSelection() is { } requestedId &&
            World.FindEntity(requestedId) is { } requested)
            Renderer?.Gizmos.Selection = requested;
        ExplorerTreeBuilder.Publish(World, Renderer?.Gizmos.Selection);
        InspectorStateBuilder.Publish(Renderer?.Gizmos.Selection);

        var renderer = Renderer;
        if (renderer is null)
            return;

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

        // Selects on left-click only when the click did not start a gizmo drag
        // (otherwise moving the entity would re-select the scene), only inside
        // the viewport, and not when the UI consumed the click.
        if (insideViewport && !Ui.PointerPressConsumed && Mouse.WasPressed(MouseButton.Left) && !renderer.Gizmos.IsDragging)
            renderer.Gizmos.Selection = renderer.Gizmos.Pick(World, matrices, localMouse);
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
    /// the field: the camera must not move at the same time.
    /// </summary>
    protected override bool CanMoveCamera() => !Ui.KeyboardConsumed;

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

    /// <summary>Composes the OS window title from the open document and the current route.</summary>
    private string ComposeWindowTitle()
    {
        var doc = EditorDocumentState.Title;
        if (doc.Length == 0) return $"Crowbar — {Ui.CurrentUrl}";
        return EditorDocumentState.IsDirty ? $"Crowbar — {doc} ●" : $"Crowbar — {doc}";
    }

    /// <summary>True when the cursor is inside the docked viewport rectangle (the whole window before the first layout).</summary>
    private bool IsPointerInsideViewport()
    {
        var rect = Ui.SceneViewport ?? new UiRect(0, 0, ViewportWidth, ViewportHeight);
        var mouse = Mouse.Position;
        return mouse.X >= rect.X && mouse.X <= rect.Right &&
               mouse.Y >= rect.Y && mouse.Y <= rect.Bottom;
    }

    private string DescribeGamemode()
    {
        var holder = _gamemodeHolder as GamemodeHolder;
        if (holder?.Current is not { } gamemode || _describe is null)
            return string.Empty;
        try
        {
            return _describe.Invoke(gamemode, null)?.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return "(erreur de script)";
        }
    }

    private void ResolveDescribe()
    {
        var holder = _gamemodeHolder as GamemodeHolder;
        _describe = holder?.Current?.GetType().GetMethod("Describe");
    }

    private void OnScriptReloaded(ScriptReloadedEventArgs e)
    {
        var detail = e.Mode switch
        {
            ScriptReloadMode.FastPath => $"IL fast path : {e.PatchedMethods} méthode(s) patchée(s) en {e.Duration.TotalMilliseconds:0} ms",
            ScriptReloadMode.FullReload => $"Full reload : {e.UpgradedInstances} instance(s) migrée(s) en {e.Duration.TotalMilliseconds:0} ms",
            _ => $"Chargé en {e.Duration.TotalMilliseconds:0} ms"
        };
        Console.WriteLine($"[Scripting] Hot reload OK ({e.Mode}): {detail}");
        UiNotifications.Show("Hot reload", detail, "success");
        ResolveDescribe();
    }

    private static void OnScriptReloadFailed(ScriptReloadFailedEventArgs e)
    {
        Console.WriteLine($"[Scripting] Hot reload FAILED: {e.Error.Message}");
        UiNotifications.Show("Hot reload", $"Échec : {e.Error.Message}", "error");
    }

    private int ViewportWidth =>
        Math.Max(1, Window.FramebufferWidth > 0 ? Window.FramebufferWidth : Window.Width);

    private int ViewportHeight =>
        Math.Max(1, Window.FramebufferHeight > 0 ? Window.FramebufferHeight : Window.Height);

    /// <summary>
    /// Composes the editor's filesystems: <see cref="FileSystem.Content"/> is the
    /// read-only base content (the output directory's shaders/assets plus the
    /// repo's <c>Editor/Ui</c> mounted at <c>/Ui</c> for hot reload), and
    /// <see cref="FileSystem.Project"/> is the read-write gamemode project rooted
    /// at the repo's <c>Game/</c> directory (falling back to the copy next to the
    /// executable in published builds).
    /// </summary>
    internal static void ConfigureFileSystem()
    {
        var backend = ZioFileSystem.Physical();

        var probe = new FileSystemService(backend, AppContext.BaseDirectory);
        var gameSource = probe.ResolveSystemDirectory(PathUtil.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Game"));
        var uiSource = probe.ResolveSystemDirectory(PathUtil.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Editor", "Ui"));

        var contentMounts = new Dictionary<FilePath, string>();
        if (uiSource is not null) contentMounts["/Ui"] = uiSource;

        var content = new FileSystemService(new ReadOnlyFileSystem(backend), AppContext.BaseDirectory, contentMounts);
        var projectRoot = gameSource ?? PathUtil.Combine(AppContext.BaseDirectory, "Game");
        var project = new FileSystemService(backend, projectRoot);

        FileSystem.Configure(backend, content, project);
    }
}

/// <summary>
/// Keeps the reference to the demo gamemode on the editor side: the
/// ScriptHost migrates the Current field on every full reload (like an engine
/// object), so the editor always observes the latest generation without
/// re-attaching.
/// </summary>
internal sealed class GamemodeHolder
{
    public object? Current;
}
