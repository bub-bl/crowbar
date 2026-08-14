using System.Numerics;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// Pure logic of the scale widget: hover detection on the three axis shafts
/// and scaling along the dragged axis. No GPU state — the
/// <see cref="GizmoRenderer"/> draws what this class computes. Dragging scales
/// the target's world scale along the active axis (world space), preserving
/// position and rotation.
/// </summary>
public sealed class ScaleGizmo : AxisGizmo
{
    protected override Vector3 ReadValue(in Transform world) => world.Scale;

    protected override Transform WithValue(in Transform world, Vector3 value) => world with { Scale = value };
}
