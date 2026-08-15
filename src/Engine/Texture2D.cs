using Crowbar.FileSystems;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Crowbar.Engine;

/// <summary>
/// A CPU-side 2D texture: decoded RGBA8 pixels, ready to be uploaded to the
/// GPU by the renderer. Textures are plain data — nothing here touches a
/// graphics device, so they can be loaded and cached before any backend
/// exists (or for tests). Named Texture2D to avoid clashing with the backend's
/// native texture type (Silk.NET.WebGPU.Texture).
/// </summary>
public sealed class Texture2D
{
    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    private Texture2D(string name, int width, int height, byte[] pixels)
    {
        Name = name;
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>Decodes an image file (PNG, JPEG, WebP, …) into RGBA8 pixels.</summary>
    public static Texture2D Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = FileSystem.Content.OpenRead(path);
        using var image = Image.Load<Rgba32>(stream);
        var width = image.Width;
        var height = image.Height;
        var pixels = new byte[checked(width * height * 4)];
        image.CopyPixelDataTo(pixels);
        return new Texture2D(PathUtil.GetFileNameWithoutExtension(path), width, height, pixels);
    }

    /// <summary>
    /// Creates a texture from raw RGBA8 pixels. The pixel data is copied, so
    /// the caller may reuse the buffer afterwards.
    /// </summary>
    public static Texture2D Create(string name, int width, int height, ReadOnlySpan<byte> rgba)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be positive.");
        if (rgba.Length < checked(width * height * 4))
            throw new ArgumentException("Pixel buffer is smaller than width * height * 4 bytes.", nameof(rgba));
        return new Texture2D(name, width, height, rgba[..checked(width * height * 4)].ToArray());
    }
}
