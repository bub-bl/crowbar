using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;

namespace Crowbar.Engine;

/// <summary>
/// WebGPU-backed <see cref="IBindGroup"/>: maps the runtime's neutral bindings
/// (buffers, textures, samplers) to native bind-group entries against the
/// pipeline's bind-group layout.
/// </summary>
public sealed unsafe class WebGpuBindGroup : IBindGroup
{
    private readonly WebGpuRuntime _runtime;
    private bool _disposed;

    internal BindGroup* BindGroup { get; private set; }

    internal WebGpuBindGroup(
        WebGpuRuntime runtime,
        WebGpuDevice device,
        BindGroupLayout* layout,
        IReadOnlyList<BindGroupBinding> bindings)
    {
        _runtime = runtime;

        BindGroupEntry* entries = stackalloc BindGroupEntry[Math.Max(1, bindings.Count)];
        for (var i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            if (binding.Buffer is WebGpuBuffer buffer)
            {
                entries[i] = new BindGroupEntry
                {
                    Binding = binding.Slot,
                    Buffer = buffer.Buffer,
                    Size = binding.BufferSize > 0 ? binding.BufferSize : buffer.Size
                };
            }
            else if (binding.Texture is WebGpuTexture texture)
            {
                entries[i] = new BindGroupEntry
                {
                    Binding = binding.Slot,
                    TextureView = texture.View
                };
            }
            else if (binding.Sampler is WebGpuSampler sampler)
            {
                entries[i] = new BindGroupEntry
                {
                    Binding = binding.Slot,
                    Sampler = sampler.Sampler
                };
            }
            else
            {
                throw new ArgumentException(
                    $"Bind group slot {binding.Slot} has no WebGPU-backed resource.", nameof(bindings));
            }
        }

        var descriptor = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = (uint)bindings.Count,
            Entries = entries
        };
        BindGroup = _runtime.Api.DeviceCreateBindGroup(device.UnsafeHandle, in descriptor);
        if (BindGroup == null)
            throw new InvalidOperationException("WebGPU could not create the bind group.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (BindGroup != null)
        {
            _runtime.Api.BindGroupRelease(BindGroup);
            BindGroup = null;
        }
    }
}
