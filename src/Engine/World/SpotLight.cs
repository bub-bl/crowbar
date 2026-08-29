using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine;

/// <summary>
/// A point light constrained to a cone along the component's world-space
/// forward direction. Intensity falls off quadratically with distance and
/// smoothly across the inner and outer cone angles.
/// </summary>
[GizmoIcon("spot-light")]
public sealed class SpotLight : Light
{
    /// <summary>Distance beyond which the light has no effect.</summary>
    [Property]
    public float Range { get; set; } = 10f;

    /// <summary>Full angle of the fully illuminated inner cone, in degrees.</summary>
    [Property]
    public float InnerConeAngle { get; set; } = 25f;

    /// <summary>Full angle where the outer cone reaches zero, in degrees.</summary>
    [Property]
    public float OuterConeAngle { get; set; } = 45f;

    /// <summary>World-space direction in which the spot light shines.</summary>
    public Vector3 Direction => World.Forward;

    /// <summary>Draws the spot light's reach and cone guide when selected.</summary>
    protected override void OnDrawGizmo()
    {
        if (Gizmos.SelectedEntity != Entity)
            return;

        Gizmos.Color = new Vector4(Color.R, Color.G, Color.B, 1f);
        var world = World;
        var origin = world.Position;
        var direction = Vector3.Normalize(world.Forward);
        var outerRadius = Range * MathF.Tan(Math.Clamp(OuterConeAngle, 0f, 179f) * MathF.PI / 360f);
        var end = origin + direction * Range;
        Gizmos.DrawCircle(end, direction, outerRadius);
        Gizmos.DrawLine(origin, end + world.Right * outerRadius);
        Gizmos.DrawLine(origin, end - world.Right * outerRadius);
        Gizmos.DrawLine(origin, end + world.Up * outerRadius);
        Gizmos.DrawLine(origin, end - world.Up * outerRadius);
    }
}
