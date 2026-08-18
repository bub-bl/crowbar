using Crowbar.FileSystems;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Crowbar.Engine;

/// <summary>
/// A CPU-side 2D texture: RGBA8 pixels, ready to be uploaded to the GPU by the
/// renderer. Textures are plain data — nothing here touches a graphics device,
/// so they can be loaded and cached before any backend exists (or for tests).
/// Named Texture2D to avoid clashing with the backend's native texture type
/// (Silk.NET.WebGPU.Texture).
///
/// File textures are decoded lazily on first access to <see cref="Width"/>,
/// <see cref="Height"/> or <see cref="Pixels"/>, so importing a model records
/// its texture set without decoding every image up front.
/// </summary>
public sealed class Texture2D
{
    private readonly object _decodeLock = new();
    private (int Width, int Height, byte[] Pixels)? _decoded;

    /// <summary>
    /// The content path this texture was loaded from, or null for textures
    /// created in code (<see cref="Create"/>). Shared cache identity:
    /// <see cref="Retain"/>/<see cref="Release"/> act on this path.
    /// </summary>
    public string? ResourcePath { get; }

    public string Name { get; }

    public int Width => EnsureDecoded().Width;
    public int Height => EnsureDecoded().Height;
    public byte[] Pixels => EnsureDecoded().Pixels;

    internal static readonly ResourceCache<Texture2D> Cache = new(CreateLazy);

    private Texture2D(string name, string? resourcePath, (int Width, int Height, byte[] Pixels)? decoded = null)
    {
        Name = name;
        ResourcePath = resourcePath;
        _decoded = decoded;
    }

    /// <summary>
    /// Opens an image file (PNG, JPEG, WebP, …) and returns its RGBA8 texture.
    /// The same path always returns the same instance, so a texture is decoded
    /// once per path and the renderer uploads a single GPU texture for it. The
    /// decode itself is deferred until the pixels are first read.
    /// </summary>
    public static Texture2D Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Cache.Load(path);
    }

    /// <summary>Records a holder reference for a file-loaded texture (no-op for created textures).</summary>
    public void Retain()
    {
        if (ResourcePath is not null)
            Cache.Retain(ResourcePath);
    }

    /// <summary>Drops a holder reference; the cache entry is discarded when the last holder releases.</summary>
    public void Release()
    {
        if (ResourcePath is not null)
            Cache.Release(ResourcePath);
    }

    /// <summary>Discards the cached texture at <paramref name="path"/> so the next load re-decodes it.</summary>
    public static void Invalidate(string path) => Cache.Invalidate(path);

    /// <summary>Discards every cached texture.</summary>
    public static void ClearCache() => Cache.Clear();

    internal static int CachedCount => Cache.Count;

    internal static int GetReferenceCount(string path) => Cache.GetReferenceCount(path);

    /// <summary>
    /// Creates the lazy cache entry: validate the file exists now (so a missing
    /// texture fails at load, like the eager path), but defer the actual decode
    /// until the renderer first reads the pixels.
    /// </summary>
    private static Texture2D CreateLazy(string path)
    {
        if (!FileSystem.Content.FileExists(path))
            throw new FileNotFoundException("Texture file not found.", path);

        return new Texture2D(PathUtil.GetFileNameWithoutExtension(path), path);
    }

    private (int Width, int Height, byte[] Pixels) EnsureDecoded()
    {
        if (_decoded is { } decoded)
            return decoded;

        lock (_decodeLock)
        {
            if (_decoded is { } current)
                return current;

            if (ResourcePath is null)
                throw new InvalidOperationException("A texture created in code has no file to decode.");

            var result = Decode(ResourcePath);
            _decoded = result;
            return result;
        }
    }

    private static (int Width, int Height, byte[] Pixels) Decode(string path)
    {
        using var stream = FileSystem.Content.OpenRead(path);
        using var image = Image.Load<Rgba32>(stream);
        var width = image.Width;
        var height = image.Height;
        var pixels = new byte[checked(width * height * 4)];
        image.CopyPixelDataTo(pixels);
        return (width, height, pixels);
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

        return new Texture2D(name, null, (width, height, rgba[..checked(width * height * 4)].ToArray()));
    }
}
