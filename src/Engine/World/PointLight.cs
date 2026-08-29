using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine;

/// <summary>
/// A light radiating from the component's world position with a finite range.
/// Beyond <see cref="Range"/> the light contributes nothing (the attenuation
/// falls off quadratically to zero at the range edge).
/// </summary>
[GizmoIcon("point-light")]
public sealed class PointLight : Light
{
    /// <summary>Distance beyond which the light has no effect.</summary>
    [Property]
    public float Range { get; set; } = 10f;

    /// <summary>Draws a wire sphere marking the light's reach when the light's entity is selected.</summary>
    protected override void OnDrawGizmo()
    {
        if (Gizmos.SelectedEntity != Entity)
            return;

        Gizmos.Color = new Vector4(Color.R, Color.G, Color.B, 1f);
        Gizmos.DrawSphere(World.Position, Range);
    }
}
