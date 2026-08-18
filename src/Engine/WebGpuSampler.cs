using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;

namespace Crowbar.Engine;

/// <summary>
/// WebGPU-backed <see cref="ISampler"/>: maps the engine-neutral
/// <see cref="SamplerDescription"/> (filter, address mode, mip filter) to the
/// native sampler descriptor.
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

    internal static WebGpuSampler Create(WebGpuRuntime runtime, WebGpuDevice device, SamplerDescription description)
    {
        description ??= new SamplerDescription();
        var descriptor = new SamplerDescriptor
        {
            AddressModeU = ToNative(description.AddressMode),
            AddressModeV = ToNative(description.AddressMode),
            AddressModeW = ToNative(description.AddressMode),
            MagFilter = ToNative(description.Filter),
            MinFilter = ToNative(description.Filter),
            MipmapFilter = description.MipmapFilter == SamplerFilter.Nearest
                ? MipmapFilterMode.Nearest
                : MipmapFilterMode.Linear,
            LodMinClamp = 0,
            LodMaxClamp = 32,
            MaxAnisotropy = (ushort)Math.Clamp(Math.Max(1, description.MaxAnisotropy), 1, 16),
            Compare = description.Compare is { } compare
                ? WebGpuNative.ToNative(compare)
                : Silk.NET.WebGPU.CompareFunction.Undefined
        };
        var sampler = runtime.Api.DeviceCreateSampler(device.UnsafeHandle, in descriptor);
        if (sampler == null)
            throw new InvalidOperationException("WebGPU could not create the sampler.");

        return new WebGpuSampler(runtime, sampler);
    }

    private static AddressMode ToNative(SamplerAddressMode mode) => mode switch
    {
        SamplerAddressMode.ClampToEdge => AddressMode.ClampToEdge,
        SamplerAddressMode.Repeat => AddressMode.Repeat,
        SamplerAddressMode.MirrorRepeat => AddressMode.MirrorRepeat,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static FilterMode ToNative(SamplerFilter filter) => filter switch
    {
        SamplerFilter.Nearest => FilterMode.Nearest,
        SamplerFilter.Linear => FilterMode.Linear,
        _ => throw new ArgumentOutOfRangeException(nameof(filter))
    };

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
