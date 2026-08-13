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

    /// <summary>
    /// Uploads raw pixels (e.g. a CPU-rendered UI or icon atlas) into a sub-rect
    /// of the texture. Only valid for textures created with <see cref="TextureDescription.CopyDestination"/>.
    /// </summary>
    void Write(nint source, int sourceRowBytes, int x, int y, int width, int height);
}
