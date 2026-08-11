namespace Crowbar.UI;

public enum PointerButton
{
    Left,
    Right,
    Middle,
    X1,
    X2
}

public readonly record struct PointerMoveEvent(float X, float Y);
public readonly record struct PointerButtonEvent(float X, float Y, PointerButton Button, bool IsDown);
public readonly record struct PointerWheelEvent(float X, float Y, float DeltaX, float DeltaY);
public readonly record struct KeyEvent(int KeyCode, bool IsDown, bool IsRepeat, string? Text = null);
/// <summary>Wheel delta delivered to a <see cref="Panel"/>'s PointerWheel handler.</summary>
public readonly record struct WheelEvent(float DeltaX, float DeltaY);
