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

    /// <summary>Cursor position in window (logical) pixels.</summary>
    public static Vector2 Position => Input.CurrentMouse.Position;

    /// <summary>Cursor movement since the previous frame, in logical pixels.</summary>
    public static Vector2 Delta => Input.CurrentMouse.Delta;

    /// <summary>Accumulated wheel delta since the previous frame (notches).</summary>
    public static float WheelX => Input.CurrentMouse.WheelX;
    public static float WheelY => Input.CurrentMouse.WheelY;

    /// <summary>True while the button is physically held down.</summary>
    public static bool IsDown(MouseButton button) => Input.CurrentMouse.IsDown(button);

    /// <summary>True if the button transitioned to down this frame.</summary>
    public static bool WasPressed(MouseButton button) =>
        Input.CurrentMouse.IsDown(button) && !Input.PreviousMouse.IsDown(button);

    /// <summary>True if the button transitioned to up this frame.</summary>
    public static bool WasReleased(MouseButton button) =>
        !Input.CurrentMouse.IsDown(button) && Input.PreviousMouse.IsDown(button);

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
