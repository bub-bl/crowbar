namespace Crowbar.Engine.Rendering;

/// <summary>
/// Backend-neutral graphics device consumed by the runtime renderer. The
/// concrete backend (WebGPU today) owns the instance/device and the window
/// swapchain, and creates every resource the renderer asks for; the runtime
/// only ever talks to this interface and to the objects it returns, so it
/// stays decoupled from a specific graphics API.
/// </summary>
public interface IGraphicsDevice : IDisposable
{
    string BackendName { get; }
    int Width { get; }
    int Height { get; }

    /// <summary>The window swapchain; the renderer acquires a frame texture and presents through it.</summary>
    ISwapchain Swapchain { get; }

    ITexture CreateTexture(TextureDescription description);
    IBuffer CreateBuffer(BufferDescription description);
    ISampler CreateSampler(SamplerDescription description);
    IPipeline CreatePipeline(PipelineDescription description);
    IComputePipeline CreateComputePipeline(ComputePipelineDescription description);
    ICommandBuffer CreateCommandBuffer();

    void Resize(int width, int height);
}
