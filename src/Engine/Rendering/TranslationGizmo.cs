using System.Numerics;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// Pure logic of the translation widget: hover detection, axis dragging and
/// optional grid snapping. No GPU state — the <see cref="GizmoRenderer"/>
/// draws what this class computes, and the math is unit-tested directly.
/// Dragging moves the target's world position through its
/// <see cref="TransformComponent.World"/> setter, which fires
/// <see cref="TransformComponent.LocalChanged"/> like any other transform
/// edit.
/// </summary>
public sealed class TranslationGizmo
{
    public enum Axis
    {
        None = -1,
        X = 0,
        Y = 1,
        Z = 2
    }

    private static readonly Vector3[] WorldAxes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

    private Vector3 _origin;
    private Vector3[] _axes = WorldAxes;
    private Vector3 _dragStartAxisPoint;
    private Vector3 _dragStartPosition;
    private TransformComponent? _target;

    /// <summary>The transform the widget moves, resolved from the editor's selection.</summary>
    public TransformComponent? Target => _target;

    public Axis HoveredAxis { get; private set; } = Axis.None;

    public Axis ActiveAxis { get; private set; } = Axis.None;

    public bool IsDragging => ActiveAxis != Axis.None;

    /// <summary>Widget-space length of the shafts; the renderer scales it to a constant screen size.</summary>
    public float AxisLength { get; set; } = 1f;

    /// <summary>Hover threshold in screen pixels.</summary>
    public float HoverPixelRadius { get; set; } = 12f;

    /// <summary>Aligns the axes to the target's local rotation instead of the world.</summary>
    public bool LocalAxes { get; set; }

    /// <summary>Snap size for dragged movement in world units, or null to move freely.</summary>
    public float? SnapSize { get; set; }

    /// <summary>Widget origin in world space (the target's position).</summary>
    public Vector3 Origin => _origin;

    /// <summary>World-space direction of an axis, respecting <see cref="LocalAxes"/>.</summary>
    public Vector3 AxisDirection(Axis axis) => _axes[(int)axis];

    public void SetTarget(TransformComponent? target)
    {
        _target = target;
        if (target is null)
        {
            _origin = Vector3.Zero;
            _axes = WorldAxes;
            HoveredAxis = Axis.None;
            ActiveAxis = Axis.None;
            return;
        }

        _origin = target.World.Position;
        if (LocalAxes)
        {
            var rotation = target.World.Rotation.Quaternion;
            _axes =
            [
                Vector3.Transform(Vector3.UnitX, rotation),
                Vector3.Transform(Vector3.UnitY, rotation),
                Vector3.Transform(Vector3.UnitZ, rotation)
            ];
        }
        else
        {
            _axes = WorldAxes;
        }
    }

    /// <summary>
    /// Picks the axis whose shaft passes closest (in screen pixels) to the
    /// mouse, within <see cref="HoverPixelRadius"/>.
    /// </summary>
    public void UpdateHover(Ray ray, Vector2 mousePixels, Matrix4x4 view, Matrix4x4 projection, int width, int height)
    {
        HoveredAxis = Axis.None;
        if (_target is null || IsDragging)
            return;

        var bestDistance = float.MaxValue;
        for (var axis = Axis.X; axis <= Axis.Z; axis++)
        {
            var direction = _axes[(int)axis];
            if (!ray.ClosestPointOnAxis(_origin, direction, out var point, out var t))
                continue;
            if (t < 0f || t > AxisLength)
                continue;

            var distance = ScreenDistance(point, mousePixels, view, projection, width, height);
            if (distance <= HoverPixelRadius && distance < bestDistance)
            {
                bestDistance = distance;
                HoveredAxis = axis;
            }
        }
    }

    /// <summary>Starts a drag on the hovered axis, anchoring the movement.</summary>
    public void BeginDrag(Ray ray)
    {
        if (_target is null || HoveredAxis == Axis.None)
            return;

        ActiveAxis = HoveredAxis;
        _dragStartPosition = _target.World.Position;
        ray.ClosestPointOnAxis(_origin, _axes[(int)ActiveAxis], out _dragStartAxisPoint, out _);
    }

    /// <summary>Moves the target along the active axis by the ray's axis-projected delta.</summary>
    public void Drag(Ray ray)
    {
        if (_target is null || ActiveAxis == Axis.None)
            return;

        if (!ray.ClosestPointOnAxis(_origin, _axes[(int)ActiveAxis], out var currentPoint, out _))
            return;

        var direction = _axes[(int)ActiveAxis];
        var length = Vector3.Dot(currentPoint - _dragStartAxisPoint, direction);
        if (SnapSize is { } snap && snap > 0f)
            length = MathF.Round(length / snap) * snap;

        var target = _target;
        var position = _dragStartPosition + direction * length;
        target.World = new Transform(position, target.World.Rotation, target.World.Scale);
    }

    public void EndDrag()
    {
        ActiveAxis = Axis.None;
    }

    private static float ScreenDistance(Vector3 world, Vector2 mousePixels, Matrix4x4 view, Matrix4x4 projection, int width, int height)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), view * projection);
        if (MathF.Abs(clip.W) < 1e-6f)
            return float.MaxValue;

        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        var pixels = new Vector2(
            (ndc.X * 0.5f + 0.5f) * width,
            (1f - (ndc.Y * 0.5f + 0.5f)) * height);
        return Vector2.Distance(pixels, mousePixels);
    }
}
