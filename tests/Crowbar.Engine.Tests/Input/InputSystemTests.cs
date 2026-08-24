using Crowbar.Engine;
using Crowbar.Engine.InputSystem;
using Crowbar.Engine.Platform;
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

    public Key KeyForChar(char character) => Key.Unknown;

    public KeyboardSnapshot QueryKeyboard() => new((bool[])_keys.Clone());

    public MouseSnapshot QueryMouse() => Mouse;

    public void SetCursorPosition(float x, float y) => LastWarp = new Vector2(x, y);
    public void SetCursorVisible(bool visible) => LastCursorVisible = visible;
    public void SetCursorShape(string cursor) => LastCursorShape = cursor;
    public string? LastCursorShape { get; private set; }
    public void SetRelativeMouseMode(bool enabled) => LastRelativeMode = enabled;
    public void SetMouseGrabbed(bool grabbed) => LastMouseGrabbed = grabbed;
    public bool? LastMouseGrabbed { get; private set; }

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

[Collection("InputFacade")]
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
            Wheel = new Vector2(0.5f, 1.5f),
            Buttons = 1u << (int)MouseButton.Left
        };
        Input.Poll();

        Assert.Equal(new Vector2(10, 20), Mouse.Position);
        Assert.Equal(new Vector2(4, -2), Mouse.Delta);
        Assert.Equal(new Vector2(0.5f, 1.5f), Mouse.Wheel);
        Assert.True(Mouse.IsDown(MouseButton.Left));
        Assert.False(Mouse.IsDown(MouseButton.Right));
        Assert.True(Mouse.WasPressed(MouseButton.Left));

        _source.Mouse = _source.Mouse with { Buttons = 0 };
        Input.Poll();
        Assert.True(Mouse.WasReleased(MouseButton.Left));
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

        Mouse.SetMouseGrabbed(true);
        Assert.True(_source.LastMouseGrabbed);
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
    public void KeycodeMapping_IsLayoutAwareForLetters()
    {
        // The UI Ctrl shortcuts key on the produced character: 'a' and 'A'
        // keycodes (SDL sends the ASCII value) must both map to VK_A, on any
        // physical key/layout.
        Assert.Equal(0x41, KeyMapping.ToWindowsVk('a'));
        Assert.Equal(0x41, KeyMapping.ToWindowsVk('A'));
        Assert.Equal(0x5A, KeyMapping.ToWindowsVk('z'));
        Assert.Equal(0x51, KeyMapping.ToWindowsVk('q'));
        Assert.Equal(0x30, KeyMapping.ToWindowsVk('0'));
        Assert.Equal(0x39, KeyMapping.ToWindowsVk('9'));
    }

    [Fact]
    public void KeycodeMapping_CoversNamedKeys()
    {
        // SDLK_* named keys are SDLK_SCANCODE_MASK | scancode (0x40000000).
        Assert.Equal(0x25, KeyMapping.ToWindowsVk(0x40000000 | 80)); // Left
        Assert.Equal(0x26, KeyMapping.ToWindowsVk(0x40000000 | 82)); // Up
        Assert.Equal(0x27, KeyMapping.ToWindowsVk(0x40000000 | 79)); // Right
        Assert.Equal(0x28, KeyMapping.ToWindowsVk(0x40000000 | 81)); // Down
        Assert.Equal(0x21, KeyMapping.ToWindowsVk(0x40000000 | 75)); // PageUp
        Assert.Equal(0x22, KeyMapping.ToWindowsVk(0x40000000 | 78)); // PageDown
        Assert.Equal(0xA2, KeyMapping.ToWindowsVk(0x40000000 | 224)); // LeftCtrl
        Assert.Equal(0xA3, KeyMapping.ToWindowsVk(0x40000000 | 228)); // RightCtrl
        Assert.Equal(0x08, KeyMapping.ToWindowsVk(8)); // Backspace
        Assert.Equal(0x09, KeyMapping.ToWindowsVk(9)); // Tab
        Assert.Equal(0x0D, KeyMapping.ToWindowsVk(13)); // Enter
        Assert.Equal(0x1B, KeyMapping.ToWindowsVk(27)); // Escape
        Assert.Equal(0x20, KeyMapping.ToWindowsVk(32)); // Space
        Assert.Equal(0x2E, KeyMapping.ToWindowsVk(127)); // Delete
    }

    [Fact]
    public void SdlMouseMask_IsRemappedToMouseButtonOrder()
    {
        // SDL masks: left=bit0, middle=bit1, right=bit2, X1=bit3, X2=bit4.
        // Our MouseButton order: Left=0, Right=1, Middle=2, X1=3, X2=4.
        var remapped = SdlInputSource.RemapButtons(
            (1u << 0) | (1u << 2) | (1u << 4));
        Assert.True((remapped & (1u << (int)MouseButton.Left)) != 0);
        Assert.True((remapped & (1u << (int)MouseButton.Right)) != 0);
        Assert.True((remapped & (1u << (int)MouseButton.X2)) != 0);
        Assert.False((remapped & (1u << (int)MouseButton.Middle)) != 0);
        Assert.False((remapped & (1u << (int)MouseButton.X1)) != 0);
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
