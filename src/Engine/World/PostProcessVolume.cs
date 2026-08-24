using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine;

/// <summary>
/// A spatial post-process volume: <see cref="PostProcess"/> components attached
/// to the same entity apply while the camera is inside this box (the entity's
/// transform: position = center, scale = full extents). Effects derived from
/// <see cref="BasePostProcess{T}"/> blend their settings by camera position
/// through <c>GetWeighted</c>; the falloff near the boundary is controlled by
/// <see cref="Softness"/>.
/// </summary>
[ComponentIcon("Solar/video/Bold/video-frame")]
public sealed class PostProcessVolume : TransformComponent
{
    /// <summary>Falloff fraction near the box boundary (0 = hard edge, 1 = linear fade across the whole box).</summary>
    [Property]
    public float Softness { get; set; } = 0.2f;

    /// <summary>
    /// Evaluates the volume weight at <paramref name="worldPoint"/>: 1 deep
    /// inside, ramping to 0 at the boundary over <see cref="Softness"/>. Returns
    /// false (weight 0) when the point is outside the box.
    /// </summary>
    internal bool TryGetWeight(Vector3 worldPoint, out float weight)
    {
        var local = World.PointToLocal(worldPoint);
        var distance = Math.Max(Math.Abs(local.X), Math.Max(Math.Abs(local.Y), Math.Abs(local.Z))) * 2f;
        if (distance > 1f)
        {
            weight = 0f;
            return false;
        }

        var softness = Math.Clamp(Softness, 0f, 1f);
        weight = softness <= 0f ? 1f : Math.Clamp((1f - distance) / softness, 0f, 1f);
        return true;
    }

    /// <summary>Draws the volume box (oriented by the entity's transform) when the entity is selected.</summary>
    protected override void OnDrawGizmo()
    {
        if (Gizmos.SelectedEntity != Entity)
            return;

        Gizmos.Color = new Vector4(0.35f, 0.7f, 1f, 1f);
        var world = World;
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1) - new Vector3(0.5f);
            corners[i] = world.PointToWorld(corner);
        }

        ReadOnlySpan<(int A, int B)> edges =
        [
            (0, 1), (0, 2), (0, 4), (1, 3), (1, 5), (2, 3), (2, 6), (3, 7), (4, 5), (4, 6), (5, 7), (6, 7)
        ];
        foreach (var (a, b) in edges)
            Gizmos.DrawLine(corners[a], corners[b]);
    }
}
