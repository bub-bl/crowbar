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
