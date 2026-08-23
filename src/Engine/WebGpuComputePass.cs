using Crowbar.Engine.Rendering;

namespace Crowbar.Engine;

public sealed class WebGpuComputePass : IComputePass
{
    private readonly WebGpuCommandBuffer _owner;
    private readonly WebGpuRuntime _runtime;
    private WebGpuComputePassEncoder _handle;
    private bool _ended;

    internal WebGpuComputePass(
        WebGpuCommandBuffer owner,
        WebGpuRuntime runtime,
        WebGpuComputePassEncoder handle)
    {
        _owner = owner;
        _runtime = runtime;
        _handle = handle;
    }

    internal WebGpuComputePassEncoder Handle => _handle;

    public void SetPipeline(IComputePipeline pipeline)
    {
        EnsureActive();
        _runtime.SetComputePipeline(_handle, pipeline as WebGpuComputePipeline
            ?? throw new ArgumentException("The compute pipeline belongs to a different backend.", nameof(pipeline)));
    }

    public void SetBindGroup(IBindGroup bindGroup, uint groupIndex = 0)
    {
        EnsureActive();
        _runtime.SetComputeBindGroup(_handle, bindGroup as WebGpuBindGroup
            ?? throw new ArgumentException("The bind group belongs to a different backend.", nameof(bindGroup)), groupIndex);
    }

    public void Dispatch(uint x, uint y = 1, uint z = 1)
    {
        EnsureActive();
        _runtime.Dispatch(_handle, x, y, z);
    }

    public void Dispose()
    {
        if (_ended)
            return;
        _ended = true;
        _owner.EndPass(this);
        _handle = default;
    }

    private void EnsureActive()
    {
        if (_ended)
            throw new ObjectDisposedException(nameof(WebGpuComputePass));
    }
}
