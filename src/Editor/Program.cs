using Crowbar.Engine.Platform;
using Crowbar.Engine;
using Crowbar.UI;

namespace Crowbar.Editor;

internal static class Program
{
    public static void Main()
    {
        using IPlatform platform = new SdlPlatform();
        using var window = platform.CreateWindow(new WindowOptions(
            Title: "Crowbar",
            Width: 1280,
            Height: 720));

        WebGpuContext? webGpu = null;
        using var ui = new UiSystem();
        // Enregistrement automatique de tout le dossier Ui/ : les fichiers avec
        // @page deviennent des pages routables, les autres des composants.
        var uiDirectory = ResolveUiDirectory("");
        var registeredCount = ui.RegisterRazorComponentsFromDirectory(uiDirectory);
        Console.WriteLine($"Razor UI: registered {registeredCount} file(s) from {uiDirectory}");
        ui.NavigationChanged += url => window.SetTitle($"Crowbar — {url}");
        ui.Navigate("/");
        Console.WriteLine($"Razor UI: current page is {ui.CurrentUrl}");
        ui.WatchDirectory(uiDirectory);
        window.PointerMoved += e => ui.ProcessPointerMove(e.X, e.Y);
        window.PointerButtonChanged += e =>
        {
            if (e.IsDown) ui.ProcessPointerDown(e.X, e.Y, (int)e.Button);
            else ui.ProcessPointerUp(e.X, e.Y, (int)e.Button);
        };
        window.PointerWheelChanged += e => ui.ProcessPointerWheel(e.X, e.Y, e.DeltaX, e.DeltaY);
        window.KeyChanged += ui.ProcessKey;

        window.Loaded += () =>
        {
            Console.WriteLine("Crowbar platform initialized.");
            var framebufferWidth = window.FramebufferWidth > 0 ? window.FramebufferWidth : window.Width;
            var framebufferHeight = window.FramebufferHeight > 0 ? window.FramebufferHeight : window.Height;
            webGpu = new WebGpuContext(window.NativeHandle, framebufferWidth, framebufferHeight) { Ui = ui };
            // With a GPU compositor present, outer box-shadows and uniform
            // borders leave the Skia raster and are drawn by Decorations.wgsl
            // (the renderer only delegates the ones that are safe to composite
            // above the flat UI texture).
            ui.Renderer.GpuDecorations = true;
            ui.SetViewport(framebufferWidth, framebufferHeight);
            ui.Render();
        };
        window.Updating += delta => { ui.Update((float)delta); webGpu?.Update(delta); };
        window.Rendering += delta => webGpu?.Render(delta);
        window.Resized += (width, height) => { webGpu?.Resize(width, height); ui.SetViewport(width, height); ui.Render(); };
        window.Closing += () =>
        {
            webGpu?.Dispose();
            Console.WriteLine("Crowbar shutting down.");
        };
        window.Run();
    }

    private static string ResolveUiDirectory(string directory) => ResolveUiPath(Path.Combine("Ui", directory), Directory.Exists);

    private static string ResolveUiPath(string relativePath, Func<string, bool> sourceExists)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, relativePath);
        var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Editor", relativePath));
        return sourceExists(sourcePath) ? sourcePath : outputPath;
    }
}
