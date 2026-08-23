using System.Runtime.InteropServices;
using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;

namespace Crowbar.Engine;

public sealed unsafe class WebGpuComputePipeline : IComputePipeline
{
    private readonly WebGpuRuntime _runtime;
    private readonly WebGpuDevice _device;
    private bool _disposed;

    internal ComputePipeline* Pipeline { get; private set; }
    internal ShaderModule* ShaderModule { get; private set; }
    internal PipelineLayout* PipelineLayout { get; private set; }
    internal BindGroupLayout*[] BindGroupLayouts { get; private set; } = [];

    internal WebGpuComputePipeline(
        WebGpuRuntime runtime,
        WebGpuDevice device,
        ComputePipelineDescription description)
    {
        _runtime = runtime;
        _device = device;

        nint shaderCode = WebGpuNative.ToUtf8HGlobal(description.ShaderSource);
        nint entryPoint = WebGpuNative.ToUtf8HGlobal(description.EntryPoint);
        try
        {
            var wgsl = new ShaderModuleWGSLDescriptor { Code = (byte*)shaderCode };
            wgsl.Chain.SType = SType.ShaderModuleWgslDescriptor;
            var shaderDescriptor = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgsl };
            ShaderModule = runtime.Api.DeviceCreateShaderModule(device.UnsafeHandle, in shaderDescriptor);
            if (ShaderModule == null)
                throw new InvalidOperationException("WebGPU could not create the compute shader module.");

            var groupCount = description.BindGroups.Count;
            BindGroupLayouts = new BindGroupLayout*[groupCount];
            BindGroupLayout** nativeLayouts = stackalloc BindGroupLayout*[Math.Max(1, groupCount)];
            for (var group = 0; group < groupCount; group++)
            {
                var layout = WebGpuPipeline.CreateBindGroupLayout(runtime, device, description.BindGroups[group]);
                BindGroupLayouts[group] = layout;
                nativeLayouts[group] = layout;
            }

            var layoutDescriptor = new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = (uint)groupCount,
                BindGroupLayouts = nativeLayouts
            };
            PipelineLayout = runtime.Api.DeviceCreatePipelineLayout(device.UnsafeHandle, in layoutDescriptor);
            if (PipelineLayout == null)
                throw new InvalidOperationException("WebGPU could not create the compute pipeline layout.");

            var descriptor = new ComputePipelineDescriptor
            {
                Layout = PipelineLayout,
                Compute = new ProgrammableStageDescriptor
                {
                    Module = ShaderModule,
                    EntryPoint = (byte*)entryPoint
                }
            };
            Pipeline = runtime.Api.DeviceCreateComputePipeline(device.UnsafeHandle, in descriptor);
            if (Pipeline == null)
                throw new InvalidOperationException("WebGPU could not create the compute pipeline.");
        }
        finally
        {
            Marshal.FreeHGlobal(shaderCode);
            Marshal.FreeHGlobal(entryPoint);
        }
    }

    public IBindGroup CreateBindGroup(IReadOnlyList<BindGroupBinding> bindings) =>
        CreateBindGroup(0, bindings);

    public IBindGroup CreateBindGroup(int groupIndex, IReadOnlyList<BindGroupBinding> bindings)
    {
        if (groupIndex < 0 || groupIndex >= BindGroupLayouts.Length)
            throw new ArgumentOutOfRangeException(nameof(groupIndex));
        return new WebGpuBindGroup(_runtime, _device, BindGroupLayouts[groupIndex], bindings);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (Pipeline != null)
            _runtime.Api.ComputePipelineRelease(Pipeline);
        if (PipelineLayout != null)
            _runtime.Api.PipelineLayoutRelease(PipelineLayout);
        foreach (var layout in BindGroupLayouts)
        {
            if (layout != null)
                _runtime.Api.BindGroupLayoutRelease(layout);
        }
        if (ShaderModule != null)
            _runtime.Api.ShaderModuleRelease(ShaderModule);

        Pipeline = null;
        PipelineLayout = null;
        ShaderModule = null;
        BindGroupLayouts = [];
    }
}
