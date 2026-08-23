namespace Crowbar.Engine.Rendering;

public interface IComputePass : IDisposable
{
    void SetPipeline(IComputePipeline pipeline);
    void SetBindGroup(IBindGroup bindGroup, uint groupIndex = 0);
    void Dispatch(uint x, uint y = 1, uint z = 1);
}
