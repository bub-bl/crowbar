using Crowbar.Engine;
using Crowbar.Engine.InputSystem;
using Crowbar.UI;
using System.Numerics;
using Xunit;

namespace Crowbar.Engine.InputSystem.Tests;

/// <summary>Scriptable IInputSource used to drive the static facades in tests.</summary>
internal sealed class FakeInputSource : IInputSource
{
    private readonly bool[] _keys = new bool[(int)Key.KeyCount];
    public MouseSnapshot Mouse { get; set; }
    public bool IsWindowFocused { get; set; } = true;
    public Vector2? LastWarp { get; private set; }
    public bool? LastCursorVisible { get; private set; }
    public bool? LastRelativeMode { get; private set; }
    public int CaptureCount { get; private set; }
    public int ReleaseCount { get; private set; }

    public void SetKey(Key key, bool down) => _keys[(int)key] = down;

    public KeyboardSnapshot QueryKeyboard() => new((bool[])_keys.Clone());

    public MouseSnapshot QueryMouse() => Mouse;

    public void SetCursorPosition(float x, float y) => LastWarp = new Vector2(x, y);
    public void SetCursorVisible(bool visible) => LastCursorVisible = visible;
    public void SetRelativeMouseMode(bool enabled) => LastRelativeMode = enabled;

    public IDisposable CaptureMouse()
    {
        CaptureCount++;
        var released = false;
        return DisposableAction.Create(() =>
        {
            if (released) return;
            released = true;
            ReleaseCount++;
        });
    }

    public event Action<PointerMoveEvent>? PointerMoved;
    public event Action<PointerButtonEvent>? PointerButtonChanged;
    public event Action<PointerWheelEvent>? PointerWheelChanged;
    public event Action<KeyEvent>? KeyChanged;
}

public class InputSystemTests
{
    private readonly FakeInputSource _source = new();

    public InputSystemTests()
    {
        Input.Bind(_source);
        Input.UnbindAction("run");
        Input.UnbindAction("jump");
    }

    [Fact]
    public void ActionBinding_IsPressedWhileKeyHeld()
    {
        Input.BindAction("run", Key.LeftShift);
        Input.Poll();

        _source.SetKey(Key.LeftShift, true);
        Input.Poll();
        Assert.True(Input.IsPressed("run"));
        Assert.True(Input.IsDown(Key.LeftShift));

        _source.SetKey(Key.LeftShift, false);
        Input.Poll();
        Assert.False(Input.IsPressed("run"));
    }

    [Fact]
    public void WasPressedAndWasReleased_TrackEdges()
    {
        Input.BindAction("jump", Key.Space);
        Input.Poll();

        // First poll after binding must not report a spurious press.
        Assert.False(Input.WasPressed(Key.Space));
        Assert.False(Input.WasPressed("jump"));

        _source.SetKey(Key.Space, true);
        Input.Poll();
        Assert.True(Input.WasPressed(Key.Space));
        Assert.True(Input.WasPressed("jump"));
        Assert.True(Input.IsPressed("jump"));

        // Same frame, still held: only IsPressed, no new edge.
        Input.Poll();
        Assert.False(Input.WasPressed(Key.Space));
        Assert.True(Input.IsPressed("jump"));

        _source.SetKey(Key.Space, false);
        Input.Poll();
        Assert.True(Input.WasReleased(Key.Space));
        Assert.True(Input.WasReleased("jump"));
        Assert.False(Input.IsPressed("jump"));
    }

    [Fact]
    public void MultiKeyAction_TriggersOnAnyKey()
    {
        Input.BindAction("forward", Key.Z, Key.Up);
        Input.Poll();

        _source.SetKey(Key.Up, true);
        Input.Poll();
        Assert.True(Input.IsPressed("forward"));

        _source.SetKey(Key.Up, false);
        _source.SetKey(Key.Z, true);
        Input.Poll();
        Assert.True(Input.IsPressed("forward"));
    }

    [Fact]
    public void MouseFacade_ReportsPositionDeltaAndButtons()
    {
        Input.Poll();
        _source.Mouse = new MouseSnapshot
        {
            Position = new Vector2(10, 20),
            Delta = new Vector2(4, -2),
            WheelY = 1.5f,
            Buttons = 1u << (int)MouseButton.Left
        };
        Input.Poll();

        Assert.Equal(new Vector2(10, 20), Mouse.Position);
        Assert.Equal(new Vector2(4, -2), Mouse.Delta);
        Assert.Equal(1.5f, Mouse.WheelY);
        Assert.True(Mouse.IsDown(MouseButton.Left));
        Assert.False(Mouse.IsDown(MouseButton.Right));
        Assert.True(Input.IsMouseDown(MouseButton.Left));
        Assert.True(Input.MouseWasPressed(MouseButton.Left));

        _source.Mouse = _source.Mouse with { Buttons = 0 };
        Input.Poll();
        Assert.True(Input.MouseWasReleased(MouseButton.Left));
        Assert.False(Mouse.IsDown(MouseButton.Left));
    }

    [Fact]
    public void MouseCapture_ReturnsDisposableThatReleases()
    {
        var capture = Mouse.Capture();
        Assert.Equal(1, _source.CaptureCount);
        capture.Dispose();
        Assert.Equal(1, _source.ReleaseCount);
        capture.Dispose(); // Idempotent through the DisposableAction.
        Assert.Equal(1, _source.ReleaseCount);
    }

    [Fact]
    public void MouseControl_DelegatesToSource()
    {
        Mouse.SetCursorAt(42, 24);
        Assert.Equal(new Vector2(42, 24), _source.LastWarp);

        Mouse.SetCursorVisible(false);
        Assert.False(_source.LastCursorVisible);

        Mouse.SetRelativeMouseMode(true);
        Assert.True(_source.LastRelativeMode);
    }

    [Fact]
    public void KeyMapping_CoversEveryUiKeyCode()
    {
        Assert.Equal(0x25, KeyMapping.ToWindowsVk(Key.Left));
        Assert.Equal(0x26, KeyMapping.ToWindowsVk(Key.Up));
        Assert.Equal(0x27, KeyMapping.ToWindowsVk(Key.Right));
        Assert.Equal(0x28, KeyMapping.ToWindowsVk(Key.Down));
        Assert.Equal(0x21, KeyMapping.ToWindowsVk(Key.PageUp));
        Assert.Equal(0x22, KeyMapping.ToWindowsVk(Key.PageDown));
        Assert.Equal(0x24, KeyMapping.ToWindowsVk(Key.Home));
        Assert.Equal(0x23, KeyMapping.ToWindowsVk(Key.End));
        Assert.Equal(0x08, KeyMapping.ToWindowsVk(Key.Backspace));
        Assert.Equal(0x2E, KeyMapping.ToWindowsVk(Key.Delete));
        Assert.Equal(0x20, KeyMapping.ToWindowsVk(Key.Space));
        Assert.Equal(0x09, KeyMapping.ToWindowsVk(Key.Tab));
        Assert.Equal(0x0D, KeyMapping.ToWindowsVk(Key.Enter));
        Assert.Equal(0x1B, KeyMapping.ToWindowsVk(Key.Escape));
        Assert.Equal(0xA0, KeyMapping.ToWindowsVk(Key.LeftShift));
        Assert.Equal(0xA1, KeyMapping.ToWindowsVk(Key.RightShift));
        Assert.Equal(0xA2, KeyMapping.ToWindowsVk(Key.LeftControl));
        Assert.Equal(0xA3, KeyMapping.ToWindowsVk(Key.RightControl));
        Assert.Equal(0x12, KeyMapping.ToWindowsVk(Key.LeftAlt)); // VK_MENU

        // Ctrl+A (select-all) and the letter/digit fallback range.
        Assert.Equal(0x41, KeyMapping.ToWindowsVk(Key.A));
        Assert.Equal(0x5A, KeyMapping.ToWindowsVk(Key.Z));
        Assert.Equal(0x30, KeyMapping.ToWindowsVk(Key.D0));
        Assert.Equal(0x39, KeyMapping.ToWindowsVk(Key.D9));
    }

    [Fact]
    public void KeyEnum_MirrorsSdlScancodes()
    {
        // SDL2 scancode contract: layout-independent positions.
        Assert.Equal(4, (int)Key.A);
        Assert.Equal(26, (int)Key.W);
        Assert.Equal(29, (int)Key.Z);
        Assert.Equal(30, (int)Key.D1);
        Assert.Equal(44, (int)Key.Space);
        Assert.Equal(79, (int)Key.Right);
        Assert.Equal(80, (int)Key.Left);
        Assert.Equal(81, (int)Key.Down);
        Assert.Equal(82, (int)Key.Up);
        Assert.Equal(225, (int)Key.LeftShift);
        Assert.Equal(229, (int)Key.RightShift);
    }
}
