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
    private readonly bool _ownsView;
    private bool _disposed;

    internal Texture* Texture { get; private set; }
    internal TextureView* View { get; private set; }

    public int Width { get; }
    public int Height { get; }
    public EngineTextureFormat Format { get; }
    public Crowbar.Engine.Rendering.TextureDimension Dimension { get; }
    public int MipLevelCount { get; }
    public int ArrayLayerCount { get; }

    private WebGpuTexture(
        WebGpuRuntime runtime,
        Queue* queue,
        Texture* texture,
        TextureView* view,
        int width,
        int height,
        EngineTextureFormat format,
        Crowbar.Engine.Rendering.TextureDimension dimension,
        int mipLevelCount,
        int arrayLayerCount,
        bool ownsTexture,
        bool ownsView = true)
    {
        _runtime = runtime;
        _queue = queue;
        Texture = texture;
        View = view;
        Width = width;
        Height = height;
        Format = format;
        Dimension = dimension;
        MipLevelCount = mipLevelCount;
        ArrayLayerCount = arrayLayerCount;
        _ownsTexture = ownsTexture;
        _ownsView = ownsView;
    }

    internal static WebGpuTexture Create(
        WebGpuRuntime runtime,
        WebGpuDevice device,
        WebGpuQueue queue,
        TextureDescription description)
    {
        ValidateDescription(description);
        var usage = TextureUsage.None;
        if (description.RenderTarget) usage |= TextureUsage.RenderAttachment;
        if (description.Sampled) usage |= TextureUsage.TextureBinding;
        if (description.CopyDestination) usage |= TextureUsage.CopyDst;
        if (description.CopySource) usage |= TextureUsage.CopySrc;
        if (description.Storage) usage |= TextureUsage.StorageBinding;

        var descriptor = new TextureDescriptor
        {
            Usage = usage,
            Dimension = Silk.NET.WebGPU.TextureDimension.Dimension2D,
            Size = new Extent3D
            {
                Width = (uint)Math.Max(1, description.Width),
                Height = (uint)Math.Max(1, description.Height),
                DepthOrArrayLayers = (uint)Math.Max(1, description.ArrayLayerCount)
            },
            Format = WebGpuNative.ToNative(description.Format),
            MipLevelCount = (uint)Math.Max(1, description.MipLevelCount),
            SampleCount = (uint)Math.Max(1, description.SampleCount)
        };
        var texture = runtime.Api.DeviceCreateTexture(device.UnsafeHandle, in descriptor);
        if (texture == null)
            throw new InvalidOperationException("WebGPU could not create the texture.");

        var viewDescriptor = new TextureViewDescriptor
        {
            Format = WebGpuNative.ToNative(description.Format),
            Dimension = WebGpuNative.ToNativeViewDimension(description.Dimension),
            BaseMipLevel = 0,
            MipLevelCount = (uint)Math.Max(1, description.MipLevelCount),
            BaseArrayLayer = 0,
            ArrayLayerCount = (uint)Math.Max(1, description.ArrayLayerCount),
            Aspect = TextureAspect.All
        };
        var view = runtime.Api.TextureCreateView(texture, in viewDescriptor);
        if (view == null)
            throw new InvalidOperationException("WebGPU could not create the texture view.");

        return new WebGpuTexture(
            runtime, (Queue*)queue.NativeHandle, texture, view,
            description.Width, description.Height, description.Format, description.Dimension,
            Math.Max(1, description.MipLevelCount), Math.Max(1, description.ArrayLayerCount), ownsTexture: true);
    }

    internal static WebGpuTexture FromFrame(
        WebGpuRuntime runtime,
        Queue* queue,
        Texture* texture,
        TextureView* view,
        int width,
        int height,
        EngineTextureFormat format) =>
        new(runtime, queue, texture, view, width, height, format,
            Crowbar.Engine.Rendering.TextureDimension.Dimension2D, 1, 1, ownsTexture: false);

    public ITexture CreateView(TextureViewDescription description)
    {
        if (_disposed || Texture == null)
            throw new ObjectDisposedException(nameof(WebGpuTexture));
        ValidateViewDescription(Dimension, MipLevelCount, ArrayLayerCount, description);

        var descriptor = new TextureViewDescriptor
        {
            Format = WebGpuNative.ToNative(Format),
            Dimension = WebGpuNative.ToNativeViewDimension(description.Dimension),
            BaseMipLevel = (uint)description.BaseMipLevel,
            MipLevelCount = (uint)description.MipLevelCount,
            BaseArrayLayer = (uint)description.BaseArrayLayer,
            ArrayLayerCount = (uint)description.ArrayLayerCount,
            Aspect = TextureAspect.All
        };
        var view = _runtime.Api.TextureCreateView(Texture, in descriptor);
        if (view == null)
            throw new InvalidOperationException("WebGPU could not create the texture view.");

        return new WebGpuTexture(
            _runtime, _queue, Texture, view,
            Math.Max(1, Width >> description.BaseMipLevel),
            Math.Max(1, Height >> description.BaseMipLevel),
            Format, description.Dimension, description.MipLevelCount, description.ArrayLayerCount,
            ownsTexture: false);
    }

    internal static void ValidateDescription(TextureDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (description.Width <= 0 || description.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(description), "Texture dimensions must be positive.");
        if (description.MipLevelCount <= 0 || description.ArrayLayerCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(description), "Mip and array-layer counts must be positive.");
        if (description.Dimension == Crowbar.Engine.Rendering.TextureDimension.Dimension2D && description.ArrayLayerCount != 1)
            throw new ArgumentException("A 2D texture must have exactly one array layer.", nameof(description));
        if (description.Dimension == Crowbar.Engine.Rendering.TextureDimension.Cube && description.ArrayLayerCount != 6)
            throw new ArgumentException("A cube texture must have exactly six array layers.", nameof(description));
    }

    internal static void ValidateViewDescription(
        Crowbar.Engine.Rendering.TextureDimension textureDimension,
        int textureMipCount,
        int textureLayerCount,
        TextureViewDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (description.BaseMipLevel < 0 || description.MipLevelCount <= 0 ||
            description.BaseMipLevel + description.MipLevelCount > textureMipCount)
            throw new ArgumentOutOfRangeException(nameof(description), "The texture view mip range is invalid.");
        if (description.BaseArrayLayer < 0 || description.ArrayLayerCount <= 0 ||
            description.BaseArrayLayer + description.ArrayLayerCount > textureLayerCount)
            throw new ArgumentOutOfRangeException(nameof(description), "The texture view array-layer range is invalid.");
        if (description.Dimension == Crowbar.Engine.Rendering.TextureDimension.Cube)
        {
            if (textureDimension != Crowbar.Engine.Rendering.TextureDimension.Cube ||
                description.BaseArrayLayer != 0 || description.ArrayLayerCount != 6)
                throw new ArgumentException("A cube view must cover all six faces of a cube texture.", nameof(description));
        }
        else if (description.Dimension == Crowbar.Engine.Rendering.TextureDimension.Dimension2D &&
                 description.ArrayLayerCount != 1)
        {
            throw new ArgumentException("A 2D view must cover exactly one array layer.", nameof(description));
        }
    }

    public void Write(
        nint source,
        int sourceRowBytes,
        int x,
        int y,
        int width,
        int height,
        int mipLevel = 0,
        int arrayLayer = 0)
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
            MipLevel = (uint)Math.Max(0, mipLevel),
            Origin = new Origin3D { X = (uint)x, Y = (uint)y, Z = (uint)Math.Max(0, arrayLayer) }
        };

        // QueueWriteTexture reads the supplied pointer as the top-left pixel of
        // the data being uploaded. `source` is therefore already the first
        // pixel of the sub-image (a whole bitmap, or a pre-sliced region); the
        // destination origin (x, y) only says where to place it in the texture.
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
            _queue, in destination, (byte*)source,
            (nuint)((uint)sourceRowBytes * (uint)height), in layout, in extent);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_ownsView && View != null)
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
