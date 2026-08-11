namespace Crowbar.Engine.Rendering;

/// <summary>
/// Backend-neutral window swapchain. Each frame the renderer acquires the
/// current back-buffer texture, renders into it, presents, and releases the
/// texture. <see cref="Format"/> matches the surface format, so pipelines and
/// offscreen textures can be created against it.
/// </summary>
public interface ISwapchain : IDisposable
{
    TextureFormat Format { get; }

    /// <summary>The current back buffer, or null if no frame is available yet.</summary>
    ITexture? AcquireTexture();

    /// <summary>Presents the acquired frame and releases it for reuse.</summary>
    void Present();
}
