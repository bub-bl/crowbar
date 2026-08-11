using System.Numerics;

namespace Crowbar.Engine.InputSystem;

/// <summary>
/// Global, easy-to-reach keyboard/action input. Bind an <see cref="IInputSource"/>
/// once at startup, then query with <c>Input.IsDown(Key.W)</c> or
/// <c>Input.IsPressed("forward")</c> for action bindings. The runtime loop must
/// call <see cref="Poll"/> once per frame so the pressed/released edges track
/// frame boundaries.
/// </summary>
public static class Input
{
    private static IInputSource? _source;
    private static readonly Dictionary<string, Key[]> Actions = new(StringComparer.OrdinalIgnoreCase);
    private static KeyboardSnapshot _currentKeyboard;
    private static KeyboardSnapshot _previousKeyboard;
    private static MouseSnapshot _currentMouse;
    private static MouseSnapshot _previousMouse;

    /// <summary>Binds the platform input source backing the static facades.</summary>
    public static void Bind(IInputSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _currentMouse = default;
        _previousMouse = default;
        _previousKeyboard = default;
        _currentKeyboard = source.QueryKeyboard();
    }

    /// <summary>
    /// Advances the input state one frame: the previous snapshot is replaced by
    /// the current one and the source is polled again. Call once per frame.
    /// </summary>
    public static void Poll()
    {
        if (_source is null) return;
        _previousKeyboard = _currentKeyboard;
        _previousMouse = _currentMouse;
        _currentKeyboard = _source.QueryKeyboard();
        _currentMouse = _source.QueryMouse();
    }

    /// <summary>Binds one or more keys to a named action (case-insensitive).</summary>
    public static void BindAction(string action, params Key[] keys)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (keys is null || keys.Length == 0)
            throw new ArgumentException("An action needs at least one key.", nameof(keys));
        Actions[action] = keys;
    }

    /// <summary>Removes a previously bound action.</summary>
    public static void UnbindAction(string action) => Actions.Remove(action);

    public static bool IsDown(Key key) => _currentKeyboard.IsDown(key);

    public static bool WasPressed(Key key) => _currentKeyboard.IsDown(key) && !_previousKeyboard.IsDown(key);

    public static bool WasReleased(Key key) => !_currentKeyboard.IsDown(key) && _previousKeyboard.IsDown(key);

    /// <summary>True while any key bound to the named action is held.</summary>
    public static bool IsPressed(string action) =>
        Actions.TryGetValue(action, out var keys) && Array.Exists(keys, static key => IsDown(key));

    /// <summary>True if any bound key transitioned to down this frame.</summary>
    public static bool WasPressed(string action) =>
        Actions.TryGetValue(action, out var keys) && Array.Exists(keys, static key => WasPressed(key));

    /// <summary>True if any bound key transitioned to up this frame.</summary>
    public static bool WasReleased(string action) =>
        Actions.TryGetValue(action, out var keys) && Array.Exists(keys, static key => WasReleased(key));

    public static bool IsMouseDown(MouseButton button) => _currentMouse.IsDown(button);

    public static bool MouseWasPressed(MouseButton button) =>
        _currentMouse.IsDown(button) && !_previousMouse.IsDown(button);

    public static bool MouseWasReleased(MouseButton button) =>
        !_currentMouse.IsDown(button) && _previousMouse.IsDown(button);

    internal static IInputSource? CurrentSource => _source;

    public static Vector2 MousePosition => _currentMouse.Position;
    public static Vector2 MouseDelta => _currentMouse.Delta;
    public static float WheelX => _currentMouse.WheelX;
    public static float WheelY => _currentMouse.WheelY;
    public static bool IsWindowFocused => _source?.IsWindowFocused ?? false;
}
