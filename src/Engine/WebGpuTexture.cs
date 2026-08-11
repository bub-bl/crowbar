using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;
using EngineTextureFormat = Crowbar.Engine.Rendering.TextureFormat;

namespace Crowbar.Engine;

/// <summary>
/// WebGPU-backed <see cref="ITexture"/>: owns the native texture and its view.
/// Swapchain frame textures are owned by the surface (not the wrapper), so they
/// are created with <c>ownsTexture: false</c> and only release the view.
/// </summary>
public sealed unsafe class WebGpuTexture : ITexture
{
    private readonly WebGpuRuntime _runtime;
    private readonly Queue* _queue;
    private readonly bool _ownsTexture;
    private bool _disposed;

    internal Texture* Texture { get; private set; }
    internal TextureView* View { get; private set; }

    public int Width { get; }
    public int Height { get; }
    public EngineTextureFormat Format { get; }

    private WebGpuTexture(
        WebGpuRuntime runtime,
        Queue* queue,
        Texture* texture,
        TextureView* view,
        int width,
        int height,
        EngineTextureFormat format,
        bool ownsTexture)
    {
        _runtime = runtime;
        _queue = queue;
        Texture = texture;
        View = view;
        Width = width;
        Height = height;
        Format = format;
        _ownsTexture = ownsTexture;
    }

    internal static WebGpuTexture Create(
        WebGpuRuntime runtime,
        WebGpuDevice device,
        WebGpuQueue queue,
        TextureDescription description)
    {
        var usage = TextureUsage.None;
        if (description.RenderTarget) usage |= TextureUsage.RenderAttachment;
        if (description.Sampled) usage |= TextureUsage.TextureBinding;
        if (description.CopyDestination) usage |= TextureUsage.CopyDst;

        var descriptor = new TextureDescriptor
        {
            Usage = usage,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D
            {
                Width = (uint)Math.Max(1, description.Width),
                Height = (uint)Math.Max(1, description.Height),
                DepthOrArrayLayers = 1
            },
            Format = WebGpuNative.ToNative(description.Format),
            MipLevelCount = 1,
            SampleCount = 1
        };
        var texture = runtime.Api.DeviceCreateTexture(device.UnsafeHandle, in descriptor);
        if (texture == null)
            throw new InvalidOperationException("WebGPU could not create the texture.");

        var view = runtime.Api.TextureCreateView(texture, null);
        if (view == null)
            throw new InvalidOperationException("WebGPU could not create the texture view.");

        return new WebGpuTexture(
            runtime, (Queue*)queue.NativeHandle, texture, view,
            description.Width, description.Height, description.Format, ownsTexture: true);
    }

    internal static WebGpuTexture FromFrame(
        WebGpuRuntime runtime,
        Queue* queue,
        Texture* texture,
        TextureView* view,
        int width,
        int height,
        EngineTextureFormat format) =>
        new(runtime, queue, texture, view, width, height, format, ownsTexture: false);

    public void Write(nint source, int sourceRowBytes, int x, int y, int width, int height)
    {
        if (_disposed || Texture == null || source == 0)
            return;

        x = Math.Max(0, x);
        y = Math.Max(0, y);
        width = Math.Min(width, Width - x);
        height = Math.Min(height, Height - y);
        if (width <= 0 || height <= 0)
            return;

        var destination = new ImageCopyTexture
        {
            Texture = Texture,
            Origin = new Origin3D { X = (uint)x, Y = (uint)y, Z = 0 }
        };

        // QueueWriteTexture reads the supplied source pointer as the top-left
        // pixel of the upload extent. The caller gives us a pointer to the
        // complete bitmap, while the destination origin is the damage rect's
        // texture position; without this offset, every partial upload copies
        // pixels from (0, 0) into (x, y), duplicating the header/left column at
        // the hovered element (the exact corruption seen after the refactor).
        var bytesPerPixel = Format switch
        {
            EngineTextureFormat.Rgba8Unorm or EngineTextureFormat.Bgra8Unorm or
            EngineTextureFormat.Rgba8UnormSrgb or EngineTextureFormat.Bgra8UnormSrgb => 4,
            _ => throw new InvalidOperationException($"Texture format {Format} is not valid for pixel uploads.")
        };
        var sourceStart = (byte*)GetUploadSource(source, sourceRowBytes, x, y, bytesPerPixel);
        var layout = new TextureDataLayout
        {
            BytesPerRow = (uint)sourceRowBytes,
            RowsPerImage = (uint)height
        };
        var extent = new Extent3D
        {
            Width = (uint)width,
            Height = (uint)height,
            DepthOrArrayLayers = 1
        };
        _runtime.Api.QueueWriteTexture(
            _queue, in destination, sourceStart,
            (nuint)((uint)sourceRowBytes * (uint)height), in layout, in extent);
    }

    /// <summary>
    /// Returns the source pointer corresponding to the top-left pixel of a
    /// bitmap sub-rect. QueueWriteTexture starts reading at this pointer, not
    /// at the destination origin, so both coordinates must be applied here.
    /// </summary>
    internal static nint GetUploadSource(nint source, int sourceRowBytes, int x, int y, int bytesPerPixel)
    {
        var sourceOffset = checked((nint)y * sourceRowBytes + (nint)x * bytesPerPixel);
        return source + sourceOffset;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (View != null)
        {
            _runtime.Api.TextureViewRelease(View);
            View = null;
        }

        if (Texture != null)
        {
            if (_ownsTexture)
                _runtime.Api.TextureDestroy(Texture);
            Texture = null;
        }
    }
}
