namespace Crowbar.Engine.Rendering;

/// <summary>
/// Backend-neutral render pass: the draw commands between
/// <see cref="ICommandBuffer.BeginRenderPass"/> and <see cref="End"/>.
/// </summary>
public interface IRenderPass : IDisposable
{
    void SetPipeline(IPipeline pipeline);
    void SetBindGroup(IBindGroup bindGroup, uint groupIndex = 0);
    void SetVertexBuffer(IBuffer buffer, ulong size);

    void Draw(uint vertexCount);
    void DrawInstanced(uint vertexCount, uint instanceCount);

    void End();
}
