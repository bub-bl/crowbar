using System.Numerics;

namespace Crowbar.Engine.InputSystem;

/// <summary>
/// Global, easy-to-reach mouse facade: position/delta queries, cursor control
/// and mouse capture. Requires <see cref="Input.Bind"/> to have been called with
/// a platform <see cref="IInputSource"/>.
/// </summary>
public static class Mouse
{
    private static IInputSource? Source => Input.CurrentSource;

    public static Vector2 Position => Input.MousePosition;
    public static Vector2 Delta => Input.MouseDelta;
    public static float WheelX => Input.WheelX;
    public static float WheelY => Input.WheelY;

    public static bool IsDown(MouseButton button) => Input.IsMouseDown(button);
    public static bool WasPressed(MouseButton button) => Input.MouseWasPressed(button);
    public static bool WasReleased(MouseButton button) => Input.MouseWasReleased(button);

    /// <summary>Warps the OS cursor to the given window position (logical pixels).</summary>
    public static void SetCursorAt(float x, float y) => Source?.SetCursorPosition(x, y);

    /// <summary>Shows or hides the OS cursor while it is over the window.</summary>
    public static void SetCursorVisible(bool visible) => Source?.SetCursorVisible(visible);

    /// <summary>Enables or disables unbounded relative mouse motion (FPS style).</summary>
    public static void SetRelativeMouseMode(bool enabled) => Source?.SetRelativeMouseMode(enabled);

    /// <summary>
    /// Captures the mouse so motion keeps arriving outside the window. Dispose
    /// the returned handle (or use it with <c>using</c>) to release the capture.
    /// </summary>
    public static IDisposable Capture() => Source is null
        ? DisposableAction.Create(() => { })
        : Source.CaptureMouse();
}
