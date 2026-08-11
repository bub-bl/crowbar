namespace Crowbar.Engine.Rendering;

/// <summary>
/// Backend-neutral command recorder: one or more render passes, then a single
/// submission to the queue. The backend owns the native command encoder.
/// </summary>
public interface ICommandBuffer : IDisposable
{
    IRenderPass BeginRenderPass(RenderPassDescription description);

    /// <summary>Ends recording and submits all recorded work to the GPU queue.</summary>
    void Submit();
}
