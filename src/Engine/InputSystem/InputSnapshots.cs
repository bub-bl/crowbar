using System.Numerics;
using Crowbar.UI;

namespace Crowbar.Engine.InputSystem;

/// <summary>Immutable snapshot of the keyboard state at the start of a frame.</summary>
public readonly struct KeyboardSnapshot
{
    private readonly bool[]? _keys;

    /// <summary>Wraps a key-state array (owned by the caller).</summary>
    public KeyboardSnapshot(bool[] keys) => _keys = keys;

    /// <summary>True while <paramref name="key"/> is physically held down.</summary>
    public bool IsDown(Key key)
    {
        var index = (int)key;
        return _keys is not null && index > 0 && index < _keys.Length && _keys[index];
    }
}

/// <summary>Immutable snapshot of the mouse state at the start of a frame.</summary>
public readonly struct MouseSnapshot
{
    public Vector2 Position { get; init; }
    public Vector2 Delta { get; init; }
    public float WheelX { get; init; }
    public float WheelY { get; init; }
    public uint Buttons { get; init; }

    public bool IsDown(MouseButton button) => (Buttons & (1u << (int)button)) != 0;
}

/// <summary>
/// Raw device input behind the <see cref="Input"/> and <see cref="Mouse"/>
/// facades. A platform (SDL on desktop) implements this by polling the device
/// each frame; the runtime loop calls the facades' Poll once per frame.
/// The pointer/key/wheel events mirror the UI input contract and are raised by
/// the platform while it pumps OS events, before the state is polled.
/// </summary>
public interface IInputSource
{
    KeyboardSnapshot QueryKeyboard();
    MouseSnapshot QueryMouse();

    /// <summary>True when the application window owns the OS input focus.</summary>
    bool IsWindowFocused { get; }

    /// <summary>Resolves the physical key producing <paramref name="character"/> in the active layout.</summary>
    Key KeyForChar(char character);

    /// <summary>Warps the OS cursor to the given window position (logical pixels).</summary>
    void SetCursorPosition(float x, float y);

    /// <summary>Shows or hides the OS cursor while it is over the window.</summary>
    void SetCursorVisible(bool visible);

    /// <summary>Enables or disables SDL's relative mouse mode (unbounded deltas).</summary>
    void SetRelativeMouseMode(bool enabled);

    /// <summary>
    /// Captures the mouse so it keeps delivering motion outside the window.
    /// The returned handle releases the capture when disposed.
    /// </summary>
    IDisposable CaptureMouse();

    event Action<PointerMoveEvent>? PointerMoved;
    event Action<PointerButtonEvent>? PointerButtonChanged;
    event Action<PointerWheelEvent>? PointerWheelChanged;
    event Action<KeyEvent>? KeyChanged;
}
