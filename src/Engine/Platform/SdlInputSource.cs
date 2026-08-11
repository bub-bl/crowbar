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
    private float _wheelX;
    private float _wheelY;
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
            WheelX = _wheelX,
            WheelY = _wheelY,
            Buttons = mask
        };
        _wheelX = 0f;
        _wheelY = 0f;
        return snapshot;
    }

    /// <summary>Accumulates wheel deltas delivered by the window's SDL events.</summary>
    public void AccumulateWheel(float x, float y)
    {
        _wheelX += x;
        _wheelY += y;
    }

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
