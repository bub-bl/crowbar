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
    public float Range { get; set; } = 10f;
}
