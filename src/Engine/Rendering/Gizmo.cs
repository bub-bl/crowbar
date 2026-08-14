using System.Numerics;

namespace Crowbar.Engine.Rendering;

/// <summary>An axis of the viewport gizmo widget (X/Y/Z, or none).</summary>
public enum GizmoAxis
{
    None = -1,
    X = 0,
    Y = 1,
    Z = 2
}

/// <summary>The gizmo tool active in the viewport.</summary>
public enum GizmoMode
{
    Translate = 0,
    Rotate = 1,
    Scale = 2
}

/// <summary>
/// Pure logic shared by the viewport gizmos (translate/rotate/scale): target
/// anchoring, world-or-local axis frames, hover/active state and the drag
/// lifecycle. Concrete gizmos implement how a pointer ray moves the target;
/// the <see cref="GizmoRenderer"/> draws whatever these compute.
/// </summary>
public abstract class Gizmo
{
    protected static readonly Vector3[] WorldAxes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

    private Vector3 _origin;
    private Vector3[] _axes = WorldAxes;
    private TransformComponent? _target;

    /// <summary>The transform the widget edits, resolved from the editor's selection.</summary>
    public TransformComponent? Target => _target;

    public GizmoAxis HoveredAxis { get; protected set; } = GizmoAxis.None;

    public GizmoAxis ActiveAxis { get; protected set; } = GizmoAxis.None;

    public bool IsDragging => ActiveAxis != GizmoAxis.None;

    /// <summary>Constant on-screen size of the widget handles (shaft length, ring radius) in pixels, whatever the camera distance.</summary>
    public float ScreenSizePx { get; set; } = 100f;

    /// <summary>
    /// World-space extent that spans <paramref name="pixels"/> on screen at
    /// <paramref name="origin"/>'s depth — the exact size the renderer draws
    /// the widget at. Derived from the camera matrices so hover and rendering
    /// can never drift apart: depth comes from the view matrix (System.Numerics
    /// <c>CreateLookAt</c> is a row-vector look-at whose third <em>column</em>
    /// is -forward), FOV from the projection (M22 = 1 / tan(fov/2)).
    /// </summary>
    public static float ScreenConstantWorldSize(Vector3 origin, Matrix4x4 view, Matrix4x4 projection, int height, float pixels)
    {
        // Third column is zaxis = normalize(position - target) = -forward.
        var back = new Vector3(view.M13, view.M23, view.M33);
        var depth = Math.Max(1e-4f, -(Vector3.Dot(back, origin) + view.M43));
        var tanHalfFov = 1f / Math.Max(1e-6f, projection.M22);
        return pixels * 2f * depth * tanHalfFov / Math.Max(1, height);
    }

    /// <summary>Hover threshold in screen pixels.</summary>
    public float HoverPixelRadius { get; set; } = 12f;

    /// <summary>Aligns the axes to the target's local rotation instead of the world.</summary>
    public bool LocalAxes { get; set; }

    /// <summary>Snap size for dragged movement (world units for translate/scale, degrees for rotate), or null to move freely.</summary>
    public float? SnapSize { get; set; }

    /// <summary>Widget origin in world space (the target's position).</summary>
    public Vector3 Origin => _origin;

    /// <summary>World-space direction of an axis, respecting <see cref="LocalAxes"/>.</summary>
    public Vector3 AxisDirection(GizmoAxis axis) => _axes[(int)axis];

    public void SetTarget(TransformComponent? target)
    {
        _target = target;
        if (target is null)
        {
            _origin = Vector3.Zero;
            _axes = WorldAxes;
            HoveredAxis = GizmoAxis.None;
            ActiveAxis = GizmoAxis.None;
            return;
        }

        _origin = target.World.Position;
        _axes = LocalAxes ? AxesFromRotation(target.World.Rotation.Quaternion) : WorldAxes;
    }

    /// <summary>Picks the handle under the mouse (sets <see cref="HoveredAxis"/>).</summary>
    public abstract void UpdateHover(Ray ray, Vector2 mousePixels, Matrix4x4 view, Matrix4x4 projection, int width, int height);

    /// <summary>Starts a drag on the hovered handle, anchoring the movement.</summary>
    public abstract void BeginDrag(Ray ray);

    /// <summary>Moves the target from the ray, relative to the drag anchor.</summary>
    public abstract void Drag(Ray ray);

    public void EndDrag() => ActiveAxis = GizmoAxis.None;

    private static Vector3[] AxesFromRotation(Quaternion rotation) =>
    [
        Vector3.Transform(Vector3.UnitX, rotation),
        Vector3.Transform(Vector3.UnitY, rotation),
        Vector3.Transform(Vector3.UnitZ, rotation)
    ];

    protected static float ScreenDistance(Vector3 world, Vector2 mousePixels, Matrix4x4 view, Matrix4x4 projection, int width, int height)
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

/// <summary>
/// Shared logic of the axis-drag gizmos (translate and scale): shaft hover,
/// then a drag that projects the pointer ray onto the active axis and writes
/// a value (position or scale) along it, with optional snapping.
/// </summary>
public abstract class AxisGizmo : Gizmo
{
    private Vector3 _dragStartAxisPoint;
    private Vector3 _dragStartValue;

    /// <summary>The world-space component the gizmo edits (position for translate, scale for scale).</summary>
    protected abstract Vector3 ReadValue(in Transform world);

    /// <summary>Returns <paramref name="world"/> with <paramref name="value"/> written into the edited component.</summary>
    protected abstract Transform WithValue(in Transform world, Vector3 value);

    public override void UpdateHover(Ray ray, Vector2 mousePixels, Matrix4x4 view, Matrix4x4 projection, int width, int height)
    {
        HoveredAxis = GizmoAxis.None;
        if (Target is null || IsDragging)
            return;

        var extent = ScreenConstantWorldSize(Origin, view, projection, height, ScreenSizePx);
        var bestDistance = float.MaxValue;
        for (var axis = GizmoAxis.X; axis <= GizmoAxis.Z; axis++)
        {
            var direction = AxisDirection(axis);
            if (!ray.ClosestPointOnAxis(Origin, direction, out var point, out var t))
                continue;
            if (t < 0f || t > extent)
                continue;

            var distance = ScreenDistance(point, mousePixels, view, projection, width, height);
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
        _dragStartValue = ReadValue(Target.World);
        ray.ClosestPointOnAxis(Origin, AxisDirection(ActiveAxis), out _dragStartAxisPoint, out _);
    }

    public override void Drag(Ray ray)
    {
        if (Target is null || ActiveAxis == GizmoAxis.None)
            return;

        if (!ray.ClosestPointOnAxis(Origin, AxisDirection(ActiveAxis), out var currentPoint, out _))
            return;

        var direction = AxisDirection(ActiveAxis);
        var length = Vector3.Dot(currentPoint - _dragStartAxisPoint, direction);
        if (SnapSize is { } snap && snap > 0f)
            length = MathF.Round(length / snap) * snap;

        Target.World = WithValue(Target.World, _dragStartValue + direction * length);
    }
}
