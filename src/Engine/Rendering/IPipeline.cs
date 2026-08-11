namespace Crowbar.Engine.Rendering;

/// <summary>
/// Backend-neutral render pipeline. The backend keeps the native pipeline
/// layout internal and creates bind groups that match it, so the runtime never
/// deals with layouts.
/// </summary>
public interface IPipeline : IDisposable
{
    /// <summary>Creates a bind group compatible with this pipeline's group-0 layout.</summary>
    IBindGroup CreateBindGroup(IReadOnlyList<BindGroupBinding> bindings);

    /// <summary>Creates a bind group for <paramref name="groupIndex"/>, compatible with that group's layout.</summary>
    IBindGroup CreateBindGroup(int groupIndex, IReadOnlyList<BindGroupBinding> bindings);
}
