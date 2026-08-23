namespace Crowbar.Engine.Rendering;

/// <summary>
/// Backend-neutral GPU texture (2D, single mip). The backend owns the native
/// texture and its view; the runtime uses it as an attachment, as a sampled
/// texture in a bind group, or as an upload destination.
/// </summary>
public interface ITexture : IDisposable
{
    int Width { get; }
    int Height { get; }
    TextureFormat Format { get; }
    TextureDimension Dimension { get; }
    int MipLevelCount { get; }
    int ArrayLayerCount { get; }

    ITexture CreateView(TextureViewDescription description);

    /// <summary>
    /// Uploads raw pixels (e.g. a CPU-rendered UI or icon atlas) into a sub-rect
    /// of one mip level (0 = base). Only valid for textures created with
    /// <see cref="TextureDescription.CopyDestination"/>.
    /// </summary>
    void Write(
        nint source,
        int sourceRowBytes,
        int x,
        int y,
        int width,
        int height,
        int mipLevel = 0,
        int arrayLayer = 0);
}
