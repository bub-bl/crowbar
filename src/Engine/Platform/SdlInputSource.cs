using System.Numerics;
using Crowbar.Engine.InputSystem;
using Crowbar.UI;
using Silk.NET.SDL;

namespace Crowbar.Engine.Platform;

/// <summary>
/// SDL-backed <see cref="IInputSource"/> for a window created by
/// <see cref="SdlWindow"/>. The window pumps SDL events and raises the UI input
/// events; this source additionally polls the device state (keyboard, mouse
/// position/delta, wheel) every frame for the <see cref="Input"/> and
/// <see cref="Mouse"/> facades.
/// </summary>
internal sealed unsafe class SdlInputSource : IInputSource
{
    private readonly Sdl _sdl;
    private readonly Window* _window;
    private readonly bool[] _keyboard = new bool[(int)Key.KeyCount];
    private float _scaleX = 1f;
    private float _scaleY = 1f;
    private int _lastX;
    private int _lastY;
    private bool _hasLastPosition;
    private Vector2 _wheel;
    private bool _mouseCaptured;

    public SdlInputSource(Sdl sdl, Window* window)
    {
        _sdl = sdl;
        _window = window;
    }

    /// <summary>Window to drawable pixel ratio, refreshed on resize.</summary>
    public void SetViewportScale(float scaleX, float scaleY)
    {
        _scaleX = scaleX > 0 ? scaleX : 1f;
        _scaleY = scaleY > 0 ? scaleY : 1f;
    }

    public bool IsWindowFocused { get; private set; } = true;

    public void SetFocused(bool focused) => IsWindowFocused = focused;

    /// <summary>Resolves the physical key producing a character in the active layout.</summary>
    public Key KeyForChar(char character) =>
        (Key)(int)_sdl.GetScancodeFromKey(character);

    public KeyboardSnapshot QueryKeyboard()
    {
        var count = 0;
        var raw = _sdl.GetKeyboardState(ref count);
        var limit = Math.Min(count, _keyboard.Length);
        for (var i = 0; i < limit; i++)
            _keyboard[i] = raw[i] != 0;
        for (var i = limit; i < _keyboard.Length; i++)
            _keyboard[i] = false;
        // The buffer is reused: hand out a copy so earlier snapshots stay valid.
        return new KeyboardSnapshot((bool[])_keyboard.Clone());
    }

    public MouseSnapshot QueryMouse()
    {
        var x = 0;
        var y = 0;
        var mask = _sdl.GetMouseState(ref x, ref y);
        if (!_hasLastPosition)
        {
            _lastX = x;
            _lastY = y;
            _hasLastPosition = true;
        }

        var dx = x - _lastX;
        var dy = y - _lastY;
        _lastX = x;
        _lastY = y;

        var snapshot = new MouseSnapshot
        {
            Position = new Vector2(x * _scaleX, y * _scaleY),
            Delta = new Vector2(dx * _scaleX, dy * _scaleY),
            Wheel = _wheel,
            Buttons = RemapButtons(mask)
        };
        _wheel = Vector2.Zero;
        return snapshot;
    }

    /// <summary>
    /// SDL reports pressed buttons as SDL_BUTTON_*MASK bits (left=bit0,
    /// middle=bit1, right=bit2, X1=bit3, X2=bit4), which differ from the
    /// <see cref="MouseButton"/> order (Left, Right, Middle, X1, X2). Remap so
    /// that IsDown(Right) tests the correct bit.
    /// </summary>
    internal static uint RemapButtons(uint sdlMask)
    {
        uint result = 0;
        if ((sdlMask & (1u << 0)) != 0) result |= 1u << (int)MouseButton.Left;
        if ((sdlMask & (1u << 2)) != 0) result |= 1u << (int)MouseButton.Right;
        if ((sdlMask & (1u << 1)) != 0) result |= 1u << (int)MouseButton.Middle;
        if ((sdlMask & (1u << 3)) != 0) result |= 1u << (int)MouseButton.X1;
        if ((sdlMask & (1u << 4)) != 0) result |= 1u << (int)MouseButton.X2;
        return result;
    }

    /// <summary>Accumulates wheel movement delivered by the window's SDL events.</summary>
    public void AccumulateWheel(Vector2 delta) => _wheel += delta;

    public void SetCursorPosition(float x, float y) =>
        _sdl.WarpMouseInWindow(_window, (int)(x / _scaleX), (int)(y / _scaleY));

    public void SetCursorVisible(bool visible) =>
        _sdl.ShowCursor(visible ? 1 : 0);

    public void SetRelativeMouseMode(bool enabled) =>
        _sdl.SetRelativeMouseMode(enabled ? SdlBool.True : SdlBool.False);

    public IDisposable CaptureMouse()
    {
        _sdl.CaptureMouse(SdlBool.True);
        _mouseCaptured = true;
        return DisposableAction.Create(() =>
        {
            if (!_mouseCaptured) return;
            _mouseCaptured = false;
            _sdl.CaptureMouse(SdlBool.False);
        });
    }

    public event Action<PointerMoveEvent>? PointerMoved;
    public event Action<PointerButtonEvent>? PointerButtonChanged;
    public event Action<PointerWheelEvent>? PointerWheelChanged;
    public event Action<KeyEvent>? KeyChanged;

    // Events can only be raised from the declaring type; the owning window
    // pumps SDL events and calls these internal raisers.
    internal void RaisePointerMoved(PointerMoveEvent e) => PointerMoved?.Invoke(e);
    internal void RaisePointerButtonChanged(PointerButtonEvent e) => PointerButtonChanged?.Invoke(e);
    internal void RaisePointerWheelChanged(PointerWheelEvent e) => PointerWheelChanged?.Invoke(e);
    internal void RaiseKeyChanged(KeyEvent e) => KeyChanged?.Invoke(e);
}
