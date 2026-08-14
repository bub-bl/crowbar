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
public sealed class TranslationGizmo : AxisGizmo
{
    protected override Vector3 ReadValue(in Transform world) => world.Position;

    protected override Transform WithValue(in Transform world, Vector3 value) => world with { Position = value };
}
