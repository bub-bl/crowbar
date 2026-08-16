using System.Numerics;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// Static editor-gizmo drawing API (the analog of Unity's Gizmos): call these
/// methods from a component's <see cref="Component.OnDrawGizmo"/> override to
/// draw overlay shapes in the viewport. Calls are recorded into the batch of
/// the current scope; outside a <see cref="Begin"/> scope they are no-ops.
/// <see cref="Color"/> and <see cref="LineThickness"/> are ambient — like
/// Unity, they stay set until changed, so set both inside the hook that draws
/// with them.
/// </summary>
public static class Gizmos
{
    private static GizmoLineBatch? _batch;
    private static Entity? _selectedEntity;

    /// <summary>Color of the next shapes (straight alpha).</summary>
    public static Vector4 Color { get; set; } = new(1f, 1f, 1f, 1f);

    /// <summary>Full on-screen thickness of the next lines, in pixels.</summary>
    public static float LineThickness { get; set; } = 2f;

    /// <summary>
    /// The entity selected in the editor viewport, or null when nothing is
    /// selected. Only meaningful while drawing (inside a <see cref="Begin"/>
    /// scope): components read it from <see cref="Component.OnDrawGizmo"/> to
    /// draw only when their own entity is selected.
    /// </summary>
    public static Entity? SelectedEntity => _selectedEntity;

    /// <summary>
    /// Opens a scope that routes every <see cref="Gizmos"/> draw call into
    /// <paramref name="batch"/> and exposes <paramref name="selectedEntity"/>
    /// through <see cref="SelectedEntity"/>. Dispose the returned handle (a
    /// <see cref="DisposableAction"/>) to close the scope and restore the
    /// previous batch and selection, which makes scopes nestable.
    /// </summary>
    public static IDisposable Begin(GizmoLineBatch batch, Entity? selectedEntity = null)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var previousBatch = _batch;
        var previousSelection = _selectedEntity;
        _batch = batch;
        _selectedEntity = selectedEntity;
        var restored = false;
        return DisposableAction.Create(() =>
        {
            if (restored)
                return;
            restored = true;
            _batch = previousBatch;
            _selectedEntity = previousSelection;
        });
    }

    /// <summary>Draws a line segment.</summary>
    public static void DrawLine(Vector3 from, Vector3 to) =>
        _batch?.AddLine(from, to, Color, LineThickness);

    /// <summary>Draws a line from <paramref name="from"/> along <paramref name="direction"/>.</summary>
    public static void DrawRay(Vector3 from, Vector3 direction) =>
        _batch?.AddRay(from, direction, Color, LineThickness);

    /// <summary>Draws a wireframe sphere (three orthogonal great circles) at <paramref name="center"/>.</summary>
    public static void DrawSphere(Vector3 center, float radius, int segments = 32) =>
        _batch?.AddWireSphere(center, radius, Color, LineThickness, segments);

    /// <summary>Alias of <see cref="DrawSphere"/>: this API only rasterizes wireframe shapes.</summary>
    public static void DrawWireSphere(Vector3 center, float radius, int segments = 32) =>
        DrawSphere(center, radius, segments);

    /// <summary>Draws a circle in the plane perpendicular to <paramref name="normal"/>.</summary>
    public static void DrawCircle(Vector3 center, Vector3 normal, float radius, int segments = 32) =>
        _batch?.AddCircle(center, normal, radius, Color, LineThickness, segments);

    /// <summary>Draws the 12 edges of an axis-aligned wireframe box.</summary>
    public static void DrawWireCube(Vector3 center, Vector3 size) =>
        _batch?.AddWireCube(center, size, Color, LineThickness);

    /// <summary>Alias of <see cref="DrawWireCube"/>.</summary>
    public static void DrawBox(Vector3 center, Vector3 size) =>
        DrawWireCube(center, size);

    /// <summary>Draws a line from <paramref name="from"/> to <paramref name="to"/> with an arrowhead at <paramref name="to"/>.</summary>
    public static void DrawArrow(Vector3 from, Vector3 to) =>
        _batch?.AddArrow(from, to, Color, LineThickness);
}
