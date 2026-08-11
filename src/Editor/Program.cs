using System.Numerics;
using Crowbar.Engine;

namespace Crowbar.Editor;

internal static class Program
{
    public static void Main() => new DemoApplication().Run();
}

/// <summary>
/// Demo application: loads the Razor + Yoga + Skia + WebGPU showcase page and
/// runs it on the engine's input → update → render loop. The platform, input,
/// UI runtime and graphics device are all owned by <see cref="Application"/>.
/// </summary>
internal sealed class DemoApplication : Application
{
    protected override void OnInitialize()
    {
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

        // Enregistrement automatique de tout le dossier Ui/ : les fichiers avec
        // @page deviennent des pages routables, les autres des composants.
        var uiDirectory = ResolveUiDirectory("");
        var registeredCount = Ui.RegisterRazorComponentsFromDirectory(uiDirectory);
        Console.WriteLine($"Razor UI: registered {registeredCount} file(s) from {uiDirectory}");
        Ui.NavigationChanged += url => Window.SetTitle($"Crowbar — {url}");
        Ui.Navigate("/");
        Console.WriteLine($"Razor UI: current page is {Ui.CurrentUrl}");
        Ui.WatchDirectory(uiDirectory);
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
