using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine;

/// <summary>
/// A spatial box that adds fog density inside its bounds (the analog of
/// s&amp;box's <c>VolumetricFogVolume</c>). The <see cref="VolumetricFog"/>
/// post-process accumulates the scene's fog volumes into its froxel volume each
/// frame: a point inside a box is fogged proportionally to <see cref="Strength"/>,
/// fading toward the box edge by <see cref="FalloffExponent"/>, tinted by
/// <see cref="Color"/>. The box spans <c>[-HalfExtents, +HalfExtents]</c> in the
/// component's local space, so it can be scaled and rotated like any other
/// spatial component; the renderer packs its world-space AABB per frame.
/// </summary>
[GizmoIcon("volumetric-fog")]
public sealed class VolumetricFogVolume : TransformComponent
{
    /// <summary>Half extents of the fog box on each axis (the box spans [-Extents, +Extents]).</summary>
    [Property]
    public Vector3 HalfExtents { get; set; } = new(300f);

    /// <summary>Peak density at the box center (0 = no fog; higher = denser, obscuring more light).</summary>
    [Property]
    public float Strength { get; set; } = 1f;

    /// <summary>
    /// Exponent of the edge falloff. <c>1</c> fades density linearly from the
    /// center to the boundary; higher values keep the core denser and sharpen
    /// the edge.
    /// </summary>
    [Property]
    public float FalloffExponent { get; set; } = 1f;

    /// <summary>Tint of the light scattering inside the volume.</summary>
    [Property]
    public Vector3 Color { get; set; } = Vector3.One;

    /// <summary>
    /// Computes the world-space center and half-extents of this volume's
    /// axis-aligned bounding box (the renderer queries this each frame as it
    /// packs the fog-volume buffer).
    /// </summary>
    internal void GetWorldBounds(out Vector3 center, out Vector3 halfExtents)
    {
        // The volume spans [-HalfExtents, +HalfExtents] in local space; the
        // transform's rotation/scale are baked into an AABB by projecting the 8 corners.
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        for (var i = 0; i < 8; i++)
        {
            var bit = new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
            var local = (bit * 2f - Vector3.One) * HalfExtents;
            var world = World.PointToWorld(local);
            min = Vector3.Min(min, world);
            max = Vector3.Max(max, world);
        }

        center = (min + max) / 2f;
        halfExtents = (max - min) / 2f;
    }

    /// <summary>Draws the fog box while its entity is selected.</summary>
    protected override void OnDrawGizmo()
    {
        if (Gizmos.SelectedEntity != Entity)
            return;

        Gizmos.Color = new Vector4(0.7f, 0.9f, 1f, 0.8f);
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var bit = new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
            corners[i] = World.PointToWorld((bit * 2f - Vector3.One) * HalfExtents);
        }

        ReadOnlySpan<(int A, int B)> edges =
        [
            (0, 1), (0, 2), (0, 4), (1, 3), (1, 5), (2, 3), (2, 6), (3, 7), (4, 5), (4, 6), (5, 7), (6, 7)
        ];
        foreach (var (a, b) in edges)
            Gizmos.DrawLine(corners[a], corners[b]);
    }
}