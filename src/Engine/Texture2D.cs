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
[FileAsset]
public sealed class Texture2D : ResourceFile
{
    private readonly Lock _decodeLock = new();
    private (int Width, int Height, byte[] Pixels)? _decoded;

    /// <summary>
    /// Downsampled mip levels, index 0 = level 1 (half resolution). Built
    /// lazily on first request so textures used only at level 0 never pay
    /// for the chain.
    /// </summary>
    private readonly List<byte[]> _mips = [];

    /// <summary>
    /// The content path this texture was loaded from, or null for textures
    /// created in code (<see cref="Create"/>). Shared cache identity:
    /// <see cref="Retain"/>/<see cref="Release"/> act on this path. It is a
    /// facade over the inherited <see cref="ResourceFile.Path"/>, which is
    /// empty for created textures.
    /// </summary>
    public string? ResourcePath => string.IsNullOrEmpty(Path) ? null : Path;

    public string Name { get; private set; } = string.Empty;

    public int Width => EnsureDecoded().Width;
    public int Height => EnsureDecoded().Height;
    public byte[] Pixels => EnsureDecoded().Pixels;

    /// <summary>Total mip levels (base + each halving down to 1×1).</summary>
    public int MipLevelCount
    {
        get
        {
            var (width, height, _) = EnsureDecoded();
            return 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
        }
    }

    /// <summary>Width of mip <paramref name="level"/> (0 = base).</summary>
    public int GetMipWidth(int level)
    {
        if (level < 0)
            throw new ArgumentOutOfRangeException(nameof(level));
        return Math.Max(1, EnsureDecoded().Width >> level);
    }

    /// <summary>Height of mip <paramref name="level"/> (0 = base).</summary>
    public int GetMipHeight(int level)
    {
        if (level < 0)
            throw new ArgumentOutOfRangeException(nameof(level));
        return Math.Max(1, EnsureDecoded().Height >> level);
    }

    /// <summary>
    /// Returns the RGBA8 pixels of mip <paramref name="level"/> (0 = base),
    /// generating the chain on first request by 2×2 box-averaging the level
    /// above. Levels are cached so each is computed at most once.
    /// </summary>
    public byte[] GetMipPixels(int level)
    {
        if (level < 0)
            throw new ArgumentOutOfRangeException(nameof(level));
        if (level == 0)
            return Pixels;

        lock (_decodeLock)
        {
            var (baseWidth, baseHeight, basePixels) = EnsureDecodedLocked();
            while (_mips.Count < level)
            {
                // The level being generated is (_mips.Count + 1); its source is
                // the level below, either the base image or the previous mip.
                var sourceLevel = _mips.Count;
                var sourceWidth = Math.Max(1, baseWidth >> sourceLevel);
                var sourceHeight = Math.Max(1, baseHeight >> sourceLevel);
                var source = sourceLevel == 0 ? basePixels : _mips[sourceLevel - 1];
                _mips.Add(Downsample(sourceWidth, sourceHeight, source));
            }
            return _mips[level - 1];
        }
    }

    /// <summary>
    /// Validates the file and records its name; the actual decode stays lazy
    /// (see <see cref="Width"/>). The library allocates the instance, assigns
    /// its path and calls this; loading the same path twice returns the same
    /// instance through the shared cache.
    /// </summary>
    public override void Load()
    {
        if (!FileSystem.Content.FileExists(Path))
            throw new FileNotFoundException("Texture file not found.", Path);

        Name = PathUtil.GetFileNameWithoutExtension(Path);
        IsValid = true;
    }

    /// <summary>Allocated by the library, then populated through <see cref="Load"/>.</summary>
    private Texture2D()
    {
    }

    private Texture2D(string name, string? resourcePath, (int Width, int Height, byte[] Pixels)? decoded = null)
    {
        Name = name;
        if (resourcePath is not null)
            Path = resourcePath;
        _decoded = decoded;
    }

    /// <summary>
    /// Opens an image file (PNG, JPEG, WebP, …) and returns its RGBA8 texture,
    /// sharing the instance across every load of the same path through the
    /// global <see cref="Global.ResourceLibrary"/> cache. The decode itself is
    /// deferred until the pixels are first read.
    /// </summary>
    public static Texture2D Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ResourceLibrary.Load<Texture2D>(path);
    }

    /// <summary>Records a holder reference for a file-loaded texture (no-op for created textures).</summary>
    public void Retain()
    {
        if (ResourcePath is not null)
            ResourceLibrary.Retain<Texture2D>(ResourcePath);
    }

    /// <summary>Drops a holder reference; the cache entry is discarded when the last holder releases.</summary>
    public void Release()
    {
        if (ResourcePath is not null)
            ResourceLibrary.Release<Texture2D>(ResourcePath);
    }

    /// <summary>Discards the cached texture at <paramref name="path"/> so the next load re-decodes it.</summary>
    public static void Invalidate(string path) => ResourceLibrary.Invalidate<Texture2D>(path);

    /// <summary>Discards every cached texture.</summary>
    public static void ClearCache() => ResourceLibrary.Clear<Texture2D>();

    internal static int CachedCount => ResourceLibrary.CachedCount<Texture2D>();

    internal static int GetReferenceCount(string path) => ResourceLibrary.GetReferenceCount<Texture2D>(path);

    private (int Width, int Height, byte[] Pixels) EnsureDecoded()
    {
        if (_decoded is { } decoded)
            return decoded;

        lock (_decodeLock)
            return EnsureDecodedLocked();
    }

    /// <summary>Decodes the source image; assumes <see cref="_decodeLock"/> is held.</summary>
    private (int Width, int Height, byte[] Pixels) EnsureDecodedLocked()
    {
        if (_decoded is { } current)
            return current;

        if (ResourcePath is null)
            throw new InvalidOperationException("A texture created in code has no file to decode.");

        var result = Decode(ResourcePath);
        _decoded = result;
        return result;
    }

    /// <summary>
    /// Box-averages an RGBA8 image down to half resolution (minimum 1×1).
    /// Odd dimensions repeat the last row/column so every destination pixel
    /// still reads exactly four source texels.
    /// </summary>
    private static byte[] Downsample(int sourceWidth, int sourceHeight, byte[] source)
    {
        var dstWidth = Math.Max(1, sourceWidth / 2);
        var dstHeight = Math.Max(1, sourceHeight / 2);
        var dst = new byte[dstWidth * dstHeight * 4];

        for (var y = 0; y < dstHeight; y++)
        {
            var y0 = Math.Min(y * 2, sourceHeight - 1);
            var y1 = Math.Min(y * 2 + 1, sourceHeight - 1);
            for (var x = 0; x < dstWidth; x++)
            {
                var x0 = Math.Min(x * 2, sourceWidth - 1);
                var x1 = Math.Min(x * 2 + 1, sourceWidth - 1);
                var d = (y * dstWidth + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    var sum = source[(y0 * sourceWidth + x0) * 4 + c]
                            + source[(y0 * sourceWidth + x1) * 4 + c]
                            + source[(y1 * sourceWidth + x0) * 4 + c]
                            + source[(y1 * sourceWidth + x1) * 4 + c];
                    dst[d + c] = (byte)((sum + 2) >> 2);
                }
            }
        }

        return dst;
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
