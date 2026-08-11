using Crowbar.UI;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// Backend-neutral graphics device consumed by the runtime loop. The concrete
/// backend (WebGPU today) owns the surface, the scene pass and the Skia-UI
/// compositing; the application only talks to this interface, so the scene
/// renderer and the platform stay decoupled from a specific graphics API.
/// </summary>
public interface IGraphicsDevice : IDisposable
{
    string BackendName { get; }
    int Width { get; }
    int Height { get; }

    /// <summary>UI raster attached to this device; composited each frame.</summary>
    UiSystem? Ui { get; set; }

    void Resize(int width, int height);

    /// <summary>
    /// Renders one frame: the 3D scene with <paramref name="camera"/>, the UI
    /// raster (if any) and the final composite onto the surface.
    /// </summary>
    void Render(Camera camera, double deltaTime);
}
