using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine;

/// <summary>
/// A light with no position, illuminating the whole scene from a direction.
/// The direction is the component's world-space forward (the rotation of the
/// component's transform).
/// </summary>
[GizmoIcon("directional-light")]
public sealed class DirectionalLight : Light
{
    /// <summary>The world-space direction the light travels (pointing from the light).</summary>
    [Property]
    public Vector3 Direction => World.Rotation.Forward;

    /// <summary>Draws a unit arrow along the light direction when the light's entity is selected.</summary>
    protected internal override void OnDrawGizmo()
    {
        if (Gizmos.SelectedEntity != Entity)
            return;

        Gizmos.Color = new Vector4(Color, 1f);
        var origin = World.Position;
        Gizmos.DrawArrow(origin, origin + Direction);
    }
}
