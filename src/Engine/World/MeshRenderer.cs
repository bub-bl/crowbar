namespace Crowbar.Engine;

/// <summary>
/// Renders a <see cref="Model"/> with a <see cref="Material"/> at the
/// component's world transform (the analog of Unreal's UStaticMeshComponent,
/// s&box's ModelRenderer). The component is plain data: the renderer collects
/// these components each frame and keeps its own GPU representation, keyed by
/// this instance. A mesh renderer with a null <see cref="Model"/> draws
/// nothing.
/// </summary>
public sealed class MeshRenderer : TransformComponent
{
    /// <summary>The geometry to draw, or null to draw nothing.</summary>
    public Model? Model { get; set; }

    /// <summary>
    /// The material (a shader plus its parameter values), or null to use the
    /// engine's default material.
    /// </summary>
    public Material? Material { get; set; }
}
