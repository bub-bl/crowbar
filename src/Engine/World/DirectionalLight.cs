using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine;

/// <summary>
/// A light with no position, illuminating the whole scene from a direction.
/// The light travels along the component's world-space forward (the rotation
/// of the component's transform).
/// </summary>
[GizmoIcon("directional-light")]
public sealed class DirectionalLight : Light
{
    /// <summary>The world-space direction the light travels (the rotation's forward).</summary>
    [Property]
    public Vector3 Direction => World.Rotation.Forward;

    /// <summary>Draws a unit arrow showing where the light shines when the light's entity is selected.</summary>
    protected override void OnDrawGizmo()
    {
        if (Gizmos.SelectedEntity != Entity)
            return;

        Gizmos.Color = new Vector4(Color, 1f);
        var origin = World.Position;
        Gizmos.DrawArrow(origin, origin + Direction);
    }
}
