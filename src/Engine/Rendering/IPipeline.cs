namespace Crowbar.Engine.Rendering;

/// <summary>
/// Backend-neutral render pipeline. The backend keeps the native pipeline
/// layout internal and creates bind groups that match it, so the runtime never
/// deals with layouts.
/// </summary>
public interface IPipeline : IDisposable
{
    /// <summary>Creates a bind group compatible with this pipeline's layout.</summary>
    IBindGroup CreateBindGroup(IReadOnlyList<BindGroupBinding> bindings);
}
