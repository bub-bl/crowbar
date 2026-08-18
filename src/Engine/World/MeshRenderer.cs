namespace Crowbar.Engine;

/// <summary>
/// Renders a <see cref="Model"/> with a <see cref="Material"/> at the
/// component's world transform (the analog of Unreal's UStaticMeshComponent,
/// s&box's ModelRenderer). The component is plain data: the renderer collects
/// these components each frame and keeps its own GPU representation, keyed by
/// this instance. A mesh renderer with a null <see cref="Model"/> draws
/// nothing.
/// </summary>
[GizmoIcon("mesh")]
public sealed class MeshRenderer : TransformComponent
{
    private Model? _model;

    /// <summary>
    /// The geometry to draw, or null to draw nothing. The component holds a
    /// cache reference to the model it renders and releases it when destroyed,
    /// so a shared model stays loaded exactly as long as it is in use.
    /// </summary>
    [Property]
    public Model? Model
    {
        get => _model;
        set
        {
            if (ReferenceEquals(_model, value))
                return;
            _model?.Release();
            _model = value;
            _model?.Retain();
        }
    }

    /// <summary>
    /// The material (a shader plus its parameter values), or null to use the
    /// engine's default material.
    /// </summary>
    [Property]
    public Material? Material { get; set; }

    /// <summary>Releases the cached model reference so unused models are freed.</summary>
    protected internal override void OnDestroy()
    {
        _model?.Release();
        _model = null;
        base.OnDestroy();
    }
}
