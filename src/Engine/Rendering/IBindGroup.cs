namespace Crowbar.Engine.Rendering;

/// <summary>
/// Backend-neutral resource bindings for one pipeline, matching the layout the
/// pipeline was created with. Equivalent to a WebGPU bind group / Vulkan
/// descriptor set.
/// </summary>
public interface IBindGroup : IDisposable
{
}
