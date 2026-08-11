using Crowbar.Engine.Rendering;

namespace Crowbar.Engine;

/// <summary>
/// WebGPU-backed <see cref="IRenderPass"/>: the draw commands between
/// <see cref="WebGpuCommandBuffer.BeginRenderPass"/> and <see cref="End"/>.
/// </summary>
public sealed unsafe class WebGpuRenderPass : IRenderPass
{
    private readonly WebGpuCommandBuffer _owner;
    private readonly WebGpuRuntime _runtime;
    private WebGpuRenderPassEncoder _handle;
    private bool _ended;

    internal WebGpuRenderPass(WebGpuCommandBuffer owner, WebGpuRuntime runtime, WebGpuRenderPassEncoder handle)
    {
        _owner = owner;
        _runtime = runtime;
        _handle = handle;
    }

    internal WebGpuRenderPassEncoder Handle => _handle;

    public void SetPipeline(IPipeline pipeline)
    {
        EnsureActive();
        _runtime.SetPipeline(_handle, (WebGpuPipeline)pipeline);
    }

    public void SetBindGroup(IBindGroup bindGroup, uint groupIndex = 0)
    {
        EnsureActive();
        _runtime.SetBindGroup(_handle, (WebGpuBindGroup)bindGroup, groupIndex);
    }

    public void SetVertexBuffer(IBuffer buffer, ulong size)
    {
        EnsureActive();
        _runtime.SetVertexBuffer(_handle, (WebGpuBuffer)buffer, size);
    }

    public void SetIndexBuffer(IBuffer buffer, ulong size)
    {
        EnsureActive();
        _runtime.SetIndexBuffer(_handle, (WebGpuBuffer)buffer, size);
    }

    public void Draw(uint vertexCount)
    {
        EnsureActive();
        _runtime.Draw(_handle, vertexCount);
    }

    public void DrawIndexed(uint indexCount)
    {
        EnsureActive();
        _runtime.DrawIndexed(_handle, indexCount);
    }

    public void DrawInstanced(uint vertexCount, uint instanceCount)
    {
        EnsureActive();
        _runtime.DrawInstanced(_handle, vertexCount, instanceCount);
    }

    private void EnsureActive()
    {
        if (_ended)
            throw new InvalidOperationException("The render pass has already ended.");
    }

    public void End()
    {
        if (_ended)
            return;
        _owner.EndPass(this);
        _ended = true;
        _handle = default;
    }

    public void Dispose() => End();
}
