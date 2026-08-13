using Crowbar.Engine.Rendering;

namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// A single GPU texture into which every registered image is packed (shelf
/// packing) with a one-pixel gutter. All images therefore render through one
/// bind group and one draw call. When the atlas fills, it doubles in size and
/// every image is re-uploaded, updating the <see cref="Image2D"/> UV rects in
/// place — growth is rare and transparent to callers.
/// </summary>
internal sealed class TextureAtlas : IDisposable
{
    private const int Padding = 1;
    private const int InitialSize = 256;
    private const int MaxSize = 4096;

    private readonly IGraphicsDevice? _device;
    private readonly List<Image2D> _images = [];
    private ITexture? _texture;
    private int _size;
    private int _cursorX;
    private int _cursorY;
    private int _rowHeight;
    private bool _disposed;

    /// <summary>A null device puts the atlas in headless mode: placement and UV
    /// math still run, but no texture is created or uploaded (for tests).</summary>
    public TextureAtlas(IGraphicsDevice? device) => _device = device;

    /// <summary>The GPU texture holding every packed image (null in headless mode).</summary>
    public ITexture Texture => _texture!;

    /// <summary>Current edge length of the (square) atlas texture.</summary>
    internal int Size => _size;

    /// <summary>Registers an image, uploading it into the atlas and returning its handle.</summary>
    public Image2D Add(Texture2D source)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TextureAtlas));
        if (source.Width <= 0 || source.Height <= 0)
            throw new ArgumentException("Image dimensions must be positive.", nameof(source));

        var required = Math.Max(source.Width, source.Height) + Padding * 2;
        while (true)
        {
            if (_size > 0 && TryPack(source.Width, source.Height, out var x, out var y))
            {
                var image = new Image2D { Source = source, AtlasX = x, AtlasY = y };
                Upload(source, x, y);
                image.UvRect = ComputeUv(x, y, source.Width, source.Height);
                _images.Add(image);
                return image;
            }
            Grow(required);
            Repack();
        }
    }

    private bool TryPack(int width, int height, out int x, out int y)
    {
        var w = width + Padding * 2;
        var h = height + Padding * 2;
        if (_cursorX + w > _size)
        {
            _cursorX = 0;
            _cursorY += _rowHeight;
            _rowHeight = 0;
        }
        if (_cursorY + h > _size)
        {
            x = 0;
            y = 0;
            return false;
        }
        x = _cursorX;
        y = _cursorY;
        _cursorX += w;
        _rowHeight = Math.Max(_rowHeight, h);
        return true;
    }

    private void Grow(int required)
    {
        var next = _size == 0 ? InitialSize : _size * 2;
        while (next < required)
            next *= 2;
        if (next > MaxSize)
            throw new InvalidOperationException("The texture atlas exceeded its maximum size.");
        _size = next;
        _texture?.Dispose();
        if (_device is not null)
        {
            _texture = _device.CreateTexture(new TextureDescription
            {
                Width = _size,
                Height = _size,
                Format = TextureFormat.Rgba8Unorm,
                Sampled = true,
                CopyDestination = true
            });
        }
        _cursorX = 0;
        _cursorY = 0;
        _rowHeight = 0;
    }

    private void Repack()
    {
        _cursorX = 0;
        _cursorY = 0;
        _rowHeight = 0;
        foreach (var image in _images)
        {
            if (image.IsDisposed)
                continue;
            if (!TryPack(image.Source.Width, image.Source.Height, out var x, out var y))
                throw new InvalidOperationException("Atlas repack failed after growth.");
            image.AtlasX = x;
            image.AtlasY = y;
            Upload(image.Source, x, y);
            image.UvRect = ComputeUv(x, y, image.Source.Width, image.Source.Height);
        }
    }

    private void Upload(Texture2D source, int x, int y)
    {
        if (_texture is null)
            return;
        unsafe
        {
            fixed (byte* pixels = source.Pixels)
                _texture.Write((nint)pixels, source.Width * 4,
                    x + Padding, y + Padding, source.Width, source.Height);
        }
    }

    /// <summary>
    /// Normalized UV rect of the image, inset by half a texel so bilinear
    /// filtering never bleeds into the gutter or a neighboring image.
    /// </summary>
    private RectF ComputeUv(int x, int y, int width, int height)
    {
        var u0 = (x + Padding + 0.5f) / _size;
        var v0 = (y + Padding + 0.5f) / _size;
        var u1 = (x + Padding + width - 0.5f) / _size;
        var v1 = (y + Padding + height - 0.5f) / _size;
        return RectF.FromLTRB(u0, v0, u1, v1);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _texture?.Dispose();
        _texture = null;
        _images.Clear();
    }
}
