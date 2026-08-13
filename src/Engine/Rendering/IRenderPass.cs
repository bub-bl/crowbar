namespace Crowbar.Engine.Rendering;

/// <summary>
/// Backend-neutral render pass: the draw commands between
/// <see cref="ICommandBuffer.BeginRenderPass"/> and <see cref="End"/>.
/// </summary>
public interface IRenderPass : IDisposable
{
    void SetPipeline(IPipeline pipeline);

    /// <summary>
    /// Sets the viewport transform (NDC → pixel) and the scissor rect for the
    /// pass. Pixel-space, top-left origin; the scissor clips every subsequent
    /// draw (and clear when the backend applies it) to this rectangle.
    /// </summary>
    void SetViewport(float x, float y, float width, float height);
    void SetScissorRect(uint x, uint y, uint width, uint height);

    void SetBindGroup(IBindGroup bindGroup, uint groupIndex = 0);
    void SetVertexBuffer(IBuffer buffer, ulong size);
    void SetIndexBuffer(IBuffer buffer, ulong size);

    void Draw(uint vertexCount);
    void DrawIndexed(uint indexCount);
    void DrawInstanced(uint vertexCount, uint instanceCount);

    /// <summary>Draws <paramref name="vertexCount"/> vertices starting at <paramref name="firstVertex"/>.</summary>
    void Draw(uint vertexCount, uint firstVertex);

    /// <summary>Instanced draw starting at instance index <paramref name="firstInstance"/>.</summary>
    void DrawInstanced(uint vertexCount, uint instanceCount, uint firstInstance);

    void End();
}
