using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Crowbar.Engine;
using Crowbar.Engine.InputSystem;
using Crowbar.Engine.Rendering;
using Crowbar.Engine.Scripting;
using Crowbar.UI;

namespace Crowbar.Editor;

internal static class Program
{
    public static void Main() => new DemoApplication().Run();
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

    protected override void OnInitialize()
    {
        // Le snapping du gizmo de translation suit la taille de cellule de la grille.
        if (Renderer is { } renderer)
            renderer.Gizmos.SnapSize = renderer.Grid.CellSize;

        // Scène de démonstration : un level avec une lumière directionnelle
        // (key), une lumière ponctuelle (fill) et deux cubes rendus par des
        // MeshRenderer via le système de monde (World).
        var level = World.CreateLevel("Demo");

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

        // Un second cube plus petit, shader Unlit (3 lignes grâce aux
        // includes) : prouve la réutilisabilité de l'API shaders.
        var accent = level.SpawnEntity("Accent");
        var accentMesh = accent.AddComponent<MeshRenderer>();
        accentMesh.Model = Model.CreateCube();
        accentMesh.Material = Material.FromShader("Unlit")
            .Set("color", new Vector4(1f, 0.72f, 0.08f, 1f));
        accentMesh.Local = new Transform(
            new Vector3(2.2f, 0.9f, 1.4f),
            Rotation.FromYaw(-20f) * Rotation.FromPitch(10f),
            new Vector3(0.5f));

        // Un dernier cube sans material : il prend le material par défaut
        // (Mesh.wgsl), le chemin de repli du renderer.
        var fallback = level.SpawnEntity("DefaultCube");
        var fallbackMesh = fallback.AddComponent<MeshRenderer>();
        fallbackMesh.Model = Model.CreateCube();
        fallbackMesh.Local = new Transform(
            new Vector3(-2.2f, 0.9f, 1.4f),
            Rotation.FromYaw(-20f) * Rotation.FromPitch(10f),
            new Vector3(0.5f));

        World.Start();
        Console.WriteLine($"World: {level.Entities.Count} entité(s) dans le level '{level.Name}'.");

        // Sélection initiale : le cube principal, pour montrer le widget.
        Renderer?.Gizmos.Selection = cube;

        // Publie la hiérarchie réelle du monde pour le panneau Explorateur :
        // la page la lit via EditorExplorerState à chaque rendu.
        ExplorerTreeBuilder.Publish(World, Renderer?.Gizmos.Selection);

        // Enregistrement automatique de tout le dossier Ui/ : les fichiers avec
        // @page deviennent des pages routables, les autres des composants.
        var uiDirectory = ResolveUiDirectory("");
        var registeredCount = Ui.RegisterRazorComponentsFromDirectory(uiDirectory);
        Console.WriteLine($"Razor UI: registered {registeredCount} file(s) from {uiDirectory}");
        // Pré-compilation parallèle : le premier rendu (Navigate) ne fait plus
        // que des cache hits. Mesuré pour valider les gains.
        var precompileWatch = Stopwatch.StartNew();
        Ui.PrecompileAll();
        Console.WriteLine($"Razor UI: precompiled in {precompileWatch.ElapsedMilliseconds} ms");
        Ui.NavigationChanged += url => Window.SetTitle($"Crowbar — {url}");
        var navigateWatch = Stopwatch.StartNew();
        Ui.Navigate("/editor");
        Console.WriteLine($"Razor UI: current page is {Ui.CurrentUrl} (navigate {navigateWatch.ElapsedMilliseconds} ms)");
        Ui.WatchDirectory(uiDirectory);

        // Gamemode de démo : le dossier Game/ est compilé par un ScriptHost et
        // rechargé à chaud à chaque édition (fast path IL si seuls les corps de
        // méthodes changent, sinon full reload avec migration d'état).
        _scriptHost = new ScriptHost();
        _scriptHost.Reloaded += OnScriptReloaded;
        _scriptHost.ReloadFailed += OnScriptReloadFailed;
        try
        {
            var gameDirectory = ResolveGameDirectory();
            _scriptHost.WatchDirectory(gameDirectory, "DemoGamemode");
            ((GamemodeHolder)_gamemodeHolder!).Current = _scriptHost.Current!.CreateInstance("Game.DemoGamemode");
            _scriptHost.WatchInstance(_gamemodeHolder!);
            ResolveDescribe();
            Console.WriteLine($"[Scripting] Gamemode chargé : {_scriptHost.Current!.TypesByFullName.Count} type(s) depuis {gameDirectory}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scripting] Initial script load failed: {ex.Message}");
            UiNotifications.Show("Script", "Échec du chargement initial du gamemode", "error");
        }
    }

    /// <summary>
    /// Pilote les gizmos du viewport : le clic gauche sélectionne l'entité sous
    /// le curseur (rayon CPU contre les AABB des mesh renderers), puis le drag
    /// sur un axe du widget déplace l'entité le long de cet axe, avec snapping
    /// à la grille. Le clic droit garde son rôle d'orbite caméra.
    /// </summary>
    protected override void OnUpdate(float deltaTime)
    {
        base.OnUpdate(deltaTime);

        // Live values for the editor status bar (FPS, memory, latency). The
        // editor page re-renders on a throttle and reads these statics.
        UiDiagnostics.Fps = 1f / Math.Max(1e-4f, deltaTime);
        // GC.GetTotalMemory only reports the managed heap and is not a useful
        // measure of the application's footprint. Use the process working set
        // so native/GPU allocations are included as well.
        UiDiagnostics.UsedMemoryBytes = Environment.WorkingSet;
        UiDiagnostics.TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        UiDiagnostics.PingMs = 15f; // démo : pas encore de réseau

        // Script host : applique les hot reloads détectés et nettoie les toasts.
        _scriptHost?.Update();
        UiNotifications.PruneExpired();
        UiDiagnostics.ScriptStatus = DescribeGamemode();

        // Sélection demandée depuis l'Explorateur (UI → host) : appliquée aux
        // gizmos du viewport, puis la hiérarchie réelle est republiée pour que
        // le panneau reflète l'état courant (niveaux, entités, attachements).
        if (EditorExplorerState.ConsumeRequestedSelection() is { } requestedId &&
            World.FindEntity(requestedId) is { } requested)
            Renderer?.Gizmos.Selection = requested;
        ExplorerTreeBuilder.Publish(World, Renderer?.Gizmos.Selection);

        var renderer = Renderer;
        if (renderer is null)
            return;

        // L'outil de gizmo choisi dans la toolbar du viewport est appliqué ici,
        // chaque frame : le rendu et l'interaction suivent le même mode.
        renderer.Gizmos.Mode = GizmoToolState.Mode switch
        {
            1 => GizmoMode.Rotate,
            2 => GizmoMode.Scale,
            _ => GizmoMode.Translate
        };

        // La scène 3D est rendue dans le viewport docké (pas dans toute la
        // fenêtre) : le DockArea publie son rectangle via Ui.SceneViewport, et
        // on le donne au renderer, qui ajuste l'aspect de la caméra et limite
        // la scène à ce rectangle. Avant le premier layout, on retombe sur
        // toute la fenêtre.
        var viewport = Ui.SceneViewport;
        renderer.SetSceneViewport(viewport);

        var rect = viewport ?? new UiRect(0, 0, ViewportWidth, ViewportHeight);
        var width = Math.Max(1, (int)rect.Width);
        var height = Math.Max(1, (int)rect.Height);
        var mouse = Mouse.Position;
        var localMouse = mouse - new Vector2(rect.X, rect.Y);
        var insideViewport = IsPointerInsideViewport();

        // Un clic consommé par l'UI (bouton, onglet, saisie, scrollbar) ne doit
        // ni commencer un drag de gizmo, ni sélectionner la scène en dessous.
        // Un drag déjà engagé continue même si le curseur passe sur l'UI.
        var matrices = CameraMatrices.Compute(Camera, width, height);
        if (!Ui.PointerPressConsumed || renderer.Gizmos.IsDragging)
            renderer.Gizmos.UpdateInteraction(matrices, localMouse, Mouse.IsDown(MouseButton.Left));

        // Sélectionne au clic gauche uniquement si le clic n'a pas commencé un
        // drag de gizmo (sinon déplacer l'entité re-sélectionnerait la scène),
        // uniquement dans le viewport, et pas quand l'UI a consommé le clic.
        if (insideViewport && !Ui.PointerPressConsumed && Mouse.WasPressed(MouseButton.Left) && !renderer.Gizmos.IsDragging)
            renderer.Gizmos.Selection = renderer.Gizmos.Pick(World, matrices, localMouse);
    }

    /// <summary>
    /// L'orbite caméra ne démarre que si l'appui droit commence dans le
    /// viewport et n'est pas consommé par l'UI (bouton de toolbar, onglet,
    /// saisie, scrollbar, overlay). La décision est prise à l'appui : un drag
    /// engagé dans le viewport continue même si le curseur passe sur un
    /// panneau, et un appui sur l'UI n'orbite jamais.
    /// </summary>
    protected override bool CanMouseLook()
    {
        if (Ui.PointerPressConsumed)
            return false;
        return IsPointerInsideViewport();
    }

    /// <summary>
    /// Pendant la saisie dans un champ (TextInput), les touches ZQSD/espace
    /// reviennent au champ : la caméra ne doit pas bouger en même temps.
    /// </summary>
    protected override bool CanMoveCamera() => !Ui.KeyboardConsumed;

    /// <summary>
    /// La molette zoome la caméra seulement quand le curseur est dans le
    /// viewport et hors UI : au-dessus d'un panneau scrollable ou d'un élément
    /// interactif (toolbar, onglet), la molette reste à l'UI.
    /// </summary>
    protected override bool CanZoomCamera() =>
        IsPointerInsideViewport() && !Ui.WheelConsumed;

    /// <summary>
    /// Le pan (molette du milieu) ne démarre que dans le viewport et hors UI,
    /// comme l'orbite : un appui sur un panneau ne déplace jamais la scène.
    /// </summary>
    protected override bool CanPan() =>
        IsPointerInsideViewport() && !Ui.PointerPressConsumed;

    /// <summary>
    /// Pendant l'orbite, la souris reste confinée dans le viewport : même
    /// masquée, elle ne doit pas glisser au-dessus des panneaux voisins (elle
    /// réapparaîtrait hors de la scène à la relâche). Le delta d'orbite reste
    /// illimité (mesuré avant le confinement) : pousser contre un bord
    /// continue de tourner, seul le curseur est retenu dans la scène.
    /// </summary>
    protected override UiRect? MouseLookClampRect =>
        Ui.SceneViewport ?? new UiRect(0, 0, ViewportWidth, ViewportHeight);

    /// <summary>True quand le curseur est dans le rectangle du viewport docké (toute la fenêtre avant le premier layout).</summary>
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

    private static string ResolveGameDirectory()
    {
        // src/Editor/bin/Debug/net11.0 + 5× .. = racine du repo (dossier Game/).
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Game");
        var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Game"));
        return Directory.Exists(sourcePath) ? sourcePath : outputPath;
    }

    private static string ResolveUiDirectory(string directory) =>
        ResolveUiPath(Path.Combine("Ui", directory), Directory.Exists);

    private static string ResolveUiPath(string relativePath, Func<string, bool> sourceExists)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, relativePath);
        var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Editor", relativePath));
        return sourceExists(sourcePath) ? sourcePath : outputPath;
    }
}

/// <summary>
/// Maintient la référence au gamemode de démo côté éditeur : le ScriptHost
/// migre le champ Current à chaque full reload (comme un objet moteur), donc
/// l'éditeur observe toujours la dernière génération sans se ré-attacher.
/// </summary>
internal sealed class GamemodeHolder
{
    public object? Current;
}
