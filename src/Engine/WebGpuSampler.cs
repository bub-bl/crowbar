using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;

namespace Crowbar.Engine;

/// <summary>
/// WebGPU-backed <see cref="ISampler"/>: linear filtering with clamp-to-edge
/// addressing, the sampler used by the UI compositor.
/// </summary>
public sealed unsafe class WebGpuSampler : ISampler
{
    private readonly WebGpuRuntime _runtime;
    private bool _disposed;

    internal Sampler* Sampler { get; private set; }

    private WebGpuSampler(WebGpuRuntime runtime, Sampler* sampler)
    {
        _runtime = runtime;
        Sampler = sampler;
    }

    internal static WebGpuSampler Create(WebGpuRuntime runtime, WebGpuDevice device)
    {
        var descriptor = new SamplerDescriptor
        {
            AddressModeU = AddressMode.ClampToEdge,
            AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge,
            MagFilter = FilterMode.Linear,
            MinFilter = FilterMode.Linear,
            MipmapFilter = MipmapFilterMode.Nearest,
            LodMaxClamp = 1,
            MaxAnisotropy = 1
        };
        var sampler = runtime.Api.DeviceCreateSampler(device.UnsafeHandle, in descriptor);
        if (sampler == null)
            throw new InvalidOperationException("WebGPU could not create the sampler.");

        return new WebGpuSampler(runtime, sampler);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (Sampler != null)
        {
            _runtime.Api.SamplerRelease(Sampler);
            Sampler = null;
        }
    }
}
