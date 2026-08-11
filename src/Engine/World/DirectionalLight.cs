using System.Numerics;

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
    public Vector3 Direction => World.Rotation.Forward;
}
