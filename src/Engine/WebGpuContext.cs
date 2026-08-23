using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;
using EngineTextureFormat = Crowbar.Engine.Rendering.TextureFormat;

namespace Crowbar.Engine;

/// <summary>
/// Concrete <see cref="IGraphicsDevice"/> on WebGPU (wgpu-native) for one
/// window. Owns the window surface and swapchain only; the instance, adapter,
/// device and queue come from the shared <see cref="WebGpuSharedDevice"/>, so
/// every window renders through the same GPU device while each keeps its own
/// back buffer. All frame logic (scene pass, UI compositing) lives in the
/// runtime's <see cref="Renderer"/>, so this class is purely a backend.
/// </summary>
public sealed unsafe class WebGpuContext : IGraphicsDevice
{
    public WebGpuSharedDevice Shared { get; }
    public WebGpuRuntime Runtime => Shared.Runtime;
    public WebGpuAdapter Adapter => Shared.Adapter;
    public WebGpuDevice Device => Shared.Device;
    public WebGpuQueue Queue => Shared.Queue;

    private WebGpuSwapchain _swapchain;
    private int _width;
    private int _height;
    private bool _disposed;

    public string BackendName => "WebGPU (wgpu-native)";
    public int Width => _width;
    public int Height => _height;
    public ISwapchain Swapchain => _swapchain;

    public WebGpuContext(WebGpuSharedDevice shared, nint windowHandle, int width, int height)
    {
        Shared = shared ?? throw new ArgumentNullException(nameof(shared));
        try
        {
            if (windowHandle == 0)
                throw new ArgumentException("The window does not expose a native handle.", nameof(windowHandle));
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);

            var hwndDescriptor = new SurfaceDescriptorFromWindowsHWND
            {
                Chain = new ChainedStruct { SType = SType.SurfaceDescriptorFromWindowsHwnd },
                Hwnd = (void*)windowHandle,
                Hinstance = (void*)System.Runtime.InteropServices.Marshal.GetHINSTANCE(
                    typeof(WebGpuContext).Module)
            };
            var surfaceDescriptor = new SurfaceDescriptor
            {
                NextInChain = (ChainedStruct*)&hwndDescriptor
            };
            Surface* surface = Runtime.Api.InstanceCreateSurface(Runtime.Instance.UnsafeHandle, in surfaceDescriptor);
            if (surface == null)
                throw new InvalidOperationException("WebGPU could not create a window surface.");

            var preferredFormat = Runtime.Api.SurfaceGetPreferredFormat(surface, Adapter.UnsafeHandle);
            var format = WebGpuNative.ToEngine(preferredFormat) ?? EngineTextureFormat.Bgra8Unorm;
            Log.Info($"WebGPU surface format: {preferredFormat} (engine: {format}).");

            _swapchain = new WebGpuSwapchain(
                Runtime, Device, Queue, surface, _width, _height, format);
            Log.Info("WebGPU window surface initialized.");
        }
        catch
        {
            // The surface (if created) is released by the swapchain's
            // constructor failure path; the shared device stays alive.
            throw;
        }
    }

    public ITexture CreateTexture(TextureDescription description) =>
        WebGpuTexture.Create(Runtime, Device, Queue, description);

    public IBuffer CreateBuffer(BufferDescription description) =>
        WebGpuBuffer.Create(Runtime, Device, Queue, description);

    public ISampler CreateSampler(SamplerDescription description) =>
        WebGpuSampler.Create(Runtime, Device, description);

    public IPipeline CreatePipeline(PipelineDescription description) =>
        new WebGpuPipeline(Runtime, Device, description);

    public ICommandBuffer CreateCommandBuffer() =>
        new WebGpuCommandBuffer(Runtime, Device, Queue);

    public void Resize(int width, int height)
    {
        if (_disposed)
            return;

        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _swapchain.Resize(_width, _height);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _swapchain?.Dispose();
        _swapchain = null!;
        // The shared device (instance/adapter/device/queue) is owned by the
        // application host and outlives every window.
    }
}
