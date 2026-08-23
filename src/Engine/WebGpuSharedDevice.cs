using Crowbar.Engine.Rendering;

namespace Crowbar.Engine;

/// <summary>
/// The application-wide WebGPU backend: one native instance, adapter, device
/// and queue shared by every window. Per-window <see cref="WebGpuContext"/>
/// instances create their own surface and swapchain against this device, so
/// N windows cost N surfaces (not N devices). Owned by the application host
/// and disposed after every window is gone.
/// </summary>
public sealed class WebGpuSharedDevice : IDisposable
{
    public WebGpuRuntime Runtime { get; }
    public WebGpuAdapter Adapter { get; }
    public WebGpuDevice Device { get; }
    public WebGpuQueue Queue { get; }

    private bool _disposed;

    public WebGpuSharedDevice()
    {
        Runtime = new WebGpuRuntime();
        try
        {
            // The adapter is requested without a compatible surface: one
            // adapter drives every window. Each window queries its own
            // preferred surface format against it.
            Adapter = new WebGpuAdapter(Runtime);
            Device = Adapter.CreateDevice();
            Queue = Device.GetQueue();
            Runtime.ConfigureDebugCallback(Device);
            Log.Info("WebGPU shared device initialized.");
        }
        catch
        {
            Runtime.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Device.Dispose();
        Adapter.Dispose();
        Runtime.Dispose();
    }
}
