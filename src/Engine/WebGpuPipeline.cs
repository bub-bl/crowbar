using System.Runtime.InteropServices;
using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;

namespace Crowbar.Engine;

/// <summary>
/// WebGPU-backed <see cref="IPipeline"/>. Compiles the WGSL source, builds the
/// bind-group layout declared by the description and the render pipeline, and
/// creates bind groups compatible with that layout on demand.
/// </summary>
public sealed unsafe class WebGpuPipeline : IPipeline
{
    private readonly WebGpuRuntime _runtime;
    private readonly WebGpuDevice _device;
    private bool _disposed;

    internal RenderPipeline* Pipeline { get; private set; }
    internal ShaderModule* ShaderModule { get; private set; }
    internal PipelineLayout* PipelineLayout { get; private set; }

    /// <summary>One native bind-group layout per declared group.</summary>
    internal BindGroupLayout*[] BindGroupLayouts { get; private set; } = [];

    internal WebGpuPipeline(WebGpuRuntime runtime, WebGpuDevice device, PipelineDescription description)
    {
        _runtime = runtime;
        _device = device;

        nint shaderCode = WebGpuNative.ToUtf8HGlobal(description.ShaderSource);
        nint vertexEntry = WebGpuNative.ToUtf8HGlobal(description.VertexEntryPoint);
        nint fragmentEntry = WebGpuNative.ToUtf8HGlobal(description.FragmentEntryPoint);
        try
        {
            var wgsl = new ShaderModuleWGSLDescriptor { Code = (byte*)shaderCode };
            wgsl.Chain.SType = SType.ShaderModuleWgslDescriptor;
            var shaderDescriptor = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgsl };
            ShaderModule = _runtime.Api.DeviceCreateShaderModule(_device.UnsafeHandle, in shaderDescriptor);
            if (ShaderModule == null)
                throw new InvalidOperationException("WebGPU could not create the shader module.");

            var groupCount = description.BindGroups.Count;
            BindGroupLayouts = new BindGroupLayout*[groupCount];
            BindGroupLayout** groupLayouts = stackalloc BindGroupLayout*[Math.Max(1, groupCount)];
            for (var g = 0; g < groupCount; g++)
            {
                var layout = CreateBindGroupLayout(description.BindGroups[g]);
                BindGroupLayouts[g] = layout;
                groupLayouts[g] = layout;
            }
            var pipelineLayoutDescriptor = new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = (uint)groupCount,
                BindGroupLayouts = groupLayouts
            };
            PipelineLayout = _runtime.Api.DeviceCreatePipelineLayout(
                _device.UnsafeHandle, in pipelineLayoutDescriptor);
            if (PipelineLayout == null)
                throw new InvalidOperationException("WebGPU could not create the pipeline layout.");

            // The UI compositor blends straight-alpha src-over; the scene pass
            // renders opaque. Blend is declared per pipeline in the description.
            BlendState blend = default;
            BlendState* blendPtr = null;
            if (description.AlphaBlend)
            {
                blend = new BlendState
                {
                    Color = new BlendComponent
                    {
                        Operation = BlendOperation.Add,
                        SrcFactor = BlendFactor.SrcAlpha,
                        DstFactor = BlendFactor.OneMinusSrcAlpha
                    },
                    Alpha = new BlendComponent
                    {
                        Operation = BlendOperation.Add,
                        SrcFactor = BlendFactor.One,
                        DstFactor = BlendFactor.OneMinusSrcAlpha
                    }
                };
                blendPtr = &blend;
            }

            var target = new ColorTargetState
            {
                Format = WebGpuNative.ToNative(description.ColorFormat),
                WriteMask = ColorWriteMask.All,
                Blend = blendPtr
            };
            var fragment = new FragmentState
            {
                Module = ShaderModule,
                EntryPoint = (byte*)fragmentEntry,
                TargetCount = 1,
                Targets = &target
            };

            VertexAttribute* attributes = stackalloc VertexAttribute[
                Math.Max(1, description.VertexLayout.Attributes.Length)];
            for (var i = 0; i < description.VertexLayout.Attributes.Length; i++)
            {
                var attribute = description.VertexLayout.Attributes[i];
                attributes[i] = new VertexAttribute
                {
                    Format = WebGpuNative.ToNative(attribute.Format),
                    Offset = attribute.Offset,
                    ShaderLocation = attribute.ShaderLocation
                };
            }
            var vertexBufferLayout = new VertexBufferLayout
            {
                ArrayStride = description.VertexLayout.Stride,
                StepMode = VertexStepMode.Vertex,
                AttributeCount = (uint)description.VertexLayout.Attributes.Length,
                Attributes = attributes
            };
            var vertex = new VertexState
            {
                Module = ShaderModule,
                EntryPoint = (byte*)vertexEntry,
                BufferCount = 1,
                Buffers = &vertexBufferLayout
            };

            var depthStencil = new DepthStencilState
            {
                Format = WebGpuNative.ToNative(description.DepthFormat),
                DepthWriteEnabled = description.DepthWriteEnabled,
                DepthCompare = WebGpuNative.ToNative(description.DepthCompare),
                StencilFront = new StencilFaceState { Compare = Silk.NET.WebGPU.CompareFunction.Always },
                StencilBack = new StencilFaceState { Compare = Silk.NET.WebGPU.CompareFunction.Always }
            };

            var pipelineDescriptor = new RenderPipelineDescriptor
            {
                Layout = PipelineLayout,
                Vertex = vertex,
                Primitive = new PrimitiveState
                {
                    Topology = WebGpuNative.ToNative(description.Topology),
                    FrontFace = FrontFace.Ccw,
                    CullMode = CullMode.None
                },
                DepthStencil = &depthStencil,
                Multisample = new MultisampleState { Count = (uint)Math.Max(1, description.SampleCount), Mask = 0xFFFFFFFF },
                Fragment = &fragment
            };
            Pipeline = _runtime.Api.DeviceCreateRenderPipeline(
                _device.UnsafeHandle, in pipelineDescriptor);
            if (Pipeline == null)
                throw new InvalidOperationException("WebGPU could not create the render pipeline.");
        }
        finally
        {
            Marshal.FreeHGlobal(shaderCode);
            Marshal.FreeHGlobal(vertexEntry);
            Marshal.FreeHGlobal(fragmentEntry);
        }
    }

    private BindGroupLayout* CreateBindGroupLayout(IReadOnlyList<BindGroupLayoutBinding> bindings)
    {
        BindGroupLayoutEntry* entries = stackalloc BindGroupLayoutEntry[Math.Max(1, bindings.Count)];
        for (var i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            entries[i] = new BindGroupLayoutEntry
            {
                Binding = binding.Slot,
                Visibility = WebGpuNative.ToNative(binding.Stages)
            };
            switch (binding.Type)
            {
                case BindingType.UniformBuffer:
                    entries[i].Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform };
                    break;
                case BindingType.ReadOnlyStorageBuffer:
                    entries[i].Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage };
                    break;
                case BindingType.Texture:
                    entries[i].Texture = new TextureBindingLayout
                    {
                        SampleType = TextureSampleType.Float,
                        ViewDimension = TextureViewDimension.Dimension2D
                    };
                    break;
                case BindingType.Sampler:
                    entries[i].Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering };
                    break;
            }
        }

        var layoutDescriptor = new BindGroupLayoutDescriptor
        {
            EntryCount = (uint)bindings.Count,
            Entries = entries
        };
        var layout = _runtime.Api.DeviceCreateBindGroupLayout(_device.UnsafeHandle, in layoutDescriptor);
        if (layout == null)
            throw new InvalidOperationException("WebGPU could not create the bind group layout.");
        return layout;
    }

    public IBindGroup CreateBindGroup(IReadOnlyList<BindGroupBinding> bindings) =>
        CreateBindGroup(0, bindings);

    /// <summary>Creates a bind group for <paramref name="groupIndex"/> compatible with this pipeline's layout.</summary>
    public IBindGroup CreateBindGroup(int groupIndex, IReadOnlyList<BindGroupBinding> bindings)
    {
        if (groupIndex < 0 || groupIndex >= BindGroupLayouts.Length)
            throw new ArgumentOutOfRangeException(nameof(groupIndex),
                $"Pipeline declares {BindGroupLayouts.Length} bind group(s), not {groupIndex}.");
        return new WebGpuBindGroup(_runtime, _device, BindGroupLayouts[groupIndex], bindings);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (Pipeline != null)
        {
            _runtime.Api.RenderPipelineRelease(Pipeline);
            Pipeline = null;
        }
        if (PipelineLayout != null)
        {
            _runtime.Api.PipelineLayoutRelease(PipelineLayout);
            PipelineLayout = null;
        }
        foreach (var layout in BindGroupLayouts)
        {
            if (layout != null)
                _runtime.Api.BindGroupLayoutRelease(layout);
        }
        BindGroupLayouts = [];
        if (ShaderModule != null)
        {
            _runtime.Api.ShaderModuleRelease(ShaderModule);
            ShaderModule = null;
        }
    }
}
