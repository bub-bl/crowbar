using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;
using EngineTextureFormat = Crowbar.Engine.Rendering.TextureFormat;

namespace Crowbar.Engine;

/// <summary>
/// WebGPU-backed <see cref="ISwapchain"/>: owns the window surface, configures
/// it against the device, and exposes the current back-buffer frame for
/// rendering. The frame texture is owned by the surface, so the wrapper only
/// releases its view.
/// </summary>
public sealed unsafe class WebGpuSwapchain : ISwapchain
{
    private readonly WebGpuRuntime _runtime;
    private readonly WebGpuDevice _device;
    private readonly Queue* _queue;
    private Surface* _surface;
    private int _width;
    private int _height;
    private bool _disposed;

    public EngineTextureFormat Format { get; }

    internal WebGpuSwapchain(
        WebGpuRuntime runtime,
        WebGpuDevice device,
        WebGpuQueue queue,
        Surface* surface,
        int width,
        int height,
        EngineTextureFormat format)
    {
        _runtime = runtime;
        _device = device;
        _queue = (Queue*)queue.NativeHandle;
        _surface = surface;
        Format = format;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        Configure();
    }

    public ITexture? AcquireTexture()
    {
        if (_disposed || _surface == null)
            return null;

        SurfaceTexture surfaceTexture = default;
        _runtime.Api.SurfaceGetCurrentTexture(_surface, ref surfaceTexture);
        if (surfaceTexture.Texture == null)
            return null;

        TextureView* view = _runtime.Api.TextureCreateView(surfaceTexture.Texture, null);
        if (view == null)
            return null;

        return WebGpuTexture.FromFrame(
            _runtime, _queue, surfaceTexture.Texture, view, _width, _height, Format);
    }

    public void Present()
    {
        if (_disposed || _surface == null)
            return;
        _runtime.Api.SurfacePresent(_surface);
    }

    internal void Resize(int width, int height)
    {
        if (_disposed || _surface == null)
            return;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        Configure();
    }

    private void Configure()
    {
        var configuration = new SurfaceConfiguration
        {
            Device = _device.UnsafeHandle,
            Width = (uint)_width,
            Height = (uint)_height,
            Format = WebGpuNative.ToNative(Format),
            Usage = TextureUsage.RenderAttachment,
            PresentMode = PresentMode.Fifo,
            AlphaMode = CompositeAlphaMode.Auto
        };
        _runtime.Api.SurfaceConfigure(_surface, in configuration);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_surface != null)
        {
            _runtime.Api.SurfaceRelease(_surface);
            _surface = null;
        }
    }
}
