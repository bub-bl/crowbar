using System.Numerics;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// Pure logic of the rotation widget: hover detection on the three axis rings
/// and angle dragging around the hovered axis, with optional degree snapping.
/// No GPU state — the <see cref="GizmoRenderer"/> draws what this class
/// computes. Dragging rotates the target's world transform around the active
/// axis (right-hand rule), preserving position and scale.
/// </summary>
public sealed class RotationGizmo : Gizmo
{
    private Rotation _dragStartRotation;
    private Vector3 _dragStartRadial;

    public override void UpdateHover(Ray ray, Vector2 mousePixels, Matrix4x4 view, Matrix4x4 projection, int width, int height)
    {
        HoveredAxis = GizmoAxis.None;
        if (Target is null || IsDragging)
            return;

        var radius = ScreenConstantWorldSize(Origin, view, projection, height, ScreenSizePx);
        var bestDistance = float.MaxValue;
        for (var axis = GizmoAxis.X; axis <= GizmoAxis.Z; axis++)
        {
            var normal = AxisDirection(axis);
            if (!ray.IntersectsPlane(Origin, normal, out var point))
                continue;
            var radial = Radial(point, normal);
            if (radial is null)
                continue;

            var closest = Origin + radial.Value * radius;
            var distance = ScreenDistance(closest, mousePixels, view, projection, width, height);
            if (distance <= HoverPixelRadius && distance < bestDistance)
            {
                bestDistance = distance;
                HoveredAxis = axis;
            }
        }
    }

    public override void BeginDrag(Ray ray)
    {
        if (Target is null || HoveredAxis == GizmoAxis.None)
            return;

        ActiveAxis = HoveredAxis;
        _dragStartRotation = Target.World.Rotation;
        _dragStartRadial = ray.IntersectsPlane(Origin, AxisDirection(ActiveAxis), out var point)
            ? Radial(point, AxisDirection(ActiveAxis)) ?? ReferenceRadial(AxisDirection(ActiveAxis))
            : ReferenceRadial(AxisDirection(ActiveAxis));
    }

    public override void Drag(Ray ray)
    {
        if (Target is null || ActiveAxis == GizmoAxis.None)
            return;

        var normal = AxisDirection(ActiveAxis);
        if (!ray.IntersectsPlane(Origin, normal, out var point))
            return;
        var radial = Radial(point, normal);
        if (radial is null)
            return;

        var degrees = SignedAngle(_dragStartRadial, radial.Value, normal) * (180f / MathF.PI);
        if (SnapSize is { } snap && snap > 0f)
            degrees = MathF.Round(degrees / snap) * snap;

        Target.World = Target.World with { Rotation = _dragStartRotation * Rotation.FromAxis(normal, degrees) };
    }

    /// <summary>Projects <paramref name="point"/> onto the rotation plane and returns its unit direction from the origin.</summary>
    private Vector3? Radial(Vector3 point, Vector3 normal)
    {
        var radial = point - Origin;
        radial -= normal * Vector3.Dot(radial, normal);
        var length = radial.Length();
        return length < 1e-6f ? null : radial / length;
    }

    /// <summary>An arbitrary unit vector in the rotation plane (used only when the ray is degenerate).</summary>
    private static Vector3 ReferenceRadial(Vector3 normal)
    {
        var reference = MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        return Vector3.Normalize(Vector3.Cross(normal, reference));
    }

    /// <summary>Signed angle (radians) from <paramref name="from"/> to <paramref name="to"/> around <paramref name="normal"/>.</summary>
    private static float SignedAngle(Vector3 from, Vector3 to, Vector3 normal) =>
        MathF.Atan2(Vector3.Dot(normal, Vector3.Cross(from, to)), Vector3.Dot(from, to));
}
