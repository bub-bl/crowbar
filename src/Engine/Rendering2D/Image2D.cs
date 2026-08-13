namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// How an image is fitted into its destination rect when drawn, mirroring
/// CSS <c>object-fit</c>.
/// </summary>
public enum ImageFit
{
    /// <summary>Stretch the image to the destination rect, ignoring aspect ratio.</summary>
    Stretch,

    /// <summary>Scale the whole image to fit inside the rect, preserving aspect ratio.</summary>
    Contain,

    /// <summary>Scale the image to cover the rect, preserving aspect ratio (overflow is cropped by a clip).</summary>
    Cover,

    /// <summary>Draw the image at its intrinsic size, centered in the rect.</summary>
    Center
}

/// <summary>
/// A handle to an image registered with a <see cref="Renderer2D"/>. It carries
/// the image's dimensions and its UV rectangle inside the renderer's texture
/// atlas. Handles stay valid across atlas growth (the UVs are updated in
/// place), so callers can cache them freely. Images live as long as the
/// renderer that owns them.
/// </summary>
public sealed class Image2D
{
    internal Texture2D Source = null!;
    internal int AtlasX;
    internal int AtlasY;
    internal RectF UvRect;

    /// <summary>Intrinsic pixel width of the image.</summary>
    public int Width => Source.Width;

    /// <summary>Intrinsic pixel height of the image.</summary>
    public int Height => Source.Height;

    /// <summary>True after <see cref="Dispose"/>; disposed images are skipped when drawn.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Marks the image as no longer drawable. The atlas slot is reclaimed on the next atlas growth.</summary>
    public void Dispose() => IsDisposed = true;
}
