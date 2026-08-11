using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;
using EngineTextureFormat = Crowbar.Engine.Rendering.TextureFormat;

namespace Crowbar.Engine;

/// <summary>
/// Concrete <see cref="IGraphicsDevice"/> on WebGPU (wgpu-native). Owns the
/// device, the window surface/swapchain and the queue, and creates every
/// backend-neutral resource the runtime renderer asks for. All frame logic
/// (scene pass, UI compositing) lives in the runtime's
/// <see cref="Renderer"/>, so this class is purely a backend.
/// </summary>
public sealed unsafe class WebGpuContext : IGraphicsDevice
{
    public WebGpuRuntime Runtime { get; }
    public WebGpuAdapter Adapter { get; }
    public WebGpuDevice Device { get; }
    public WebGpuQueue Queue { get; }

    private WebGpuSwapchain _swapchain;
    private int _width;
    private int _height;
    private bool _disposed;

    public string BackendName => "WebGPU (wgpu-native)";
    public int Width => _width;
    public int Height => _height;
    public ISwapchain Swapchain => _swapchain;

    public WebGpuContext(nint windowHandle, int width, int height)
    {
        Runtime = new WebGpuRuntime();
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

            Adapter = new WebGpuAdapter(Runtime, WebGpuSurface.FromNative((nint)surface));
            Device = Adapter.CreateDevice();
            Queue = Device.GetQueue();
            Runtime.ConfigureDebugCallback(Device);

            var preferredFormat = Runtime.Api.SurfaceGetPreferredFormat(surface, Adapter.UnsafeHandle);
            var format = WebGpuNative.ToEngine(preferredFormat) ?? EngineTextureFormat.Bgra8Unorm;
            Console.WriteLine($"WebGPU surface format: {preferredFormat} (engine: {format}).");

            _swapchain = new WebGpuSwapchain(
                Runtime, Device, Queue, surface, _width, _height, format);
            Console.WriteLine("WebGPU device initialized.");
        }
        catch
        {
            Runtime.Dispose();
            throw;
        }
    }

    public ITexture CreateTexture(TextureDescription description) =>
        WebGpuTexture.Create(Runtime, Device, Queue, description);

    public IBuffer CreateBuffer(BufferDescription description) =>
        WebGpuBuffer.Create(Runtime, Device, Queue, description);

    public ISampler CreateSampler(SamplerDescription description) =>
        WebGpuSampler.Create(Runtime, Device);

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
        Device.Dispose();
        Adapter.Dispose();
        Runtime.Dispose();
    }
}
