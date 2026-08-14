namespace Crowbar.UI;

/// <summary>
/// Requested viewport gizmo tool, written by the viewport toolbar and read
/// once per frame by the editor host, which applies it to the renderer's
/// gizmos. Mirrors <see cref="UiDiagnostics"/>/<see cref="UiNotifications"/>,
/// but in the UI → host direction. Values match the engine's gizmo mode
/// (0 = translate, 1 = rotate, 2 = scale).
/// </summary>
public static class GizmoToolState
{
    /// <summary>Gizmo tool index: 0 = translate, 1 = rotate, 2 = scale.</summary>
    public static int Mode;
}
