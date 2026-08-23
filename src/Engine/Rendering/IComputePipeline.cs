namespace Crowbar.Engine.Rendering;

public interface IComputePipeline : IDisposable
{
    IBindGroup CreateBindGroup(IReadOnlyList<BindGroupBinding> bindings);
    IBindGroup CreateBindGroup(int groupIndex, IReadOnlyList<BindGroupBinding> bindings);
}
