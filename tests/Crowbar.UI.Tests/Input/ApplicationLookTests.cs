using System.Numerics;
using Crowbar.Engine;
using Crowbar.Engine.InputSystem;
using Crowbar.Engine.Platform;
using Crowbar.UI;
using Xunit;

namespace Crowbar.Engine.InputSystem.Tests;

/// <summary>
/// The camera mouse-look hides the OS cursor for the duration of the orbit and
/// restores it as soon as the right button is released. These tests drive
/// <see cref="Application.LookWithMouse"/> through the static input facades
/// with a fake platform/window, so no real window is created. Shares the
/// <c>InputFacade</c> collection with <see cref="InputSystemTests"/>: both
/// mutate the static <see cref="Input"/> state, so they must not run in
/// parallel.
/// </summary>
[Collection("InputFacade")]
public class ApplicationLookTests
{
    private const uint RightDown = 1u << (int)MouseButton.Right;

    [Fact]
    public void Camera_IsAComponentOnAWorldEntity()
    {
        using var app = new LookTestApp();

        // The viewport camera is a Camera component owned by a world entity,
        // so the editor hierarchy lists it and the inspector can edit it.
        Assert.NotNull(app.TestCamera.Entity);
        Assert.Equal("Camera", app.TestCamera.Entity!.Name);
        Assert.Same(app.TestCamera, app.TestCamera.Entity!.GetComponent<Camera>());
    }

    [Fact]
    public void GenericWindowSessionKeepsTheCursorVisibleDuringLook()
    {
        var source = new FakeInputSource();
        Input.Bind(source);
        var session = new GenericLookSession();

        source.Mouse = new MouseSnapshot
        {
            Position = new Vector2(100, 100),
            Buttons = RightDown
        };
        Input.Poll();
        session.TickLook();

        // The base window session may look around, but it must not hide the
        // cursor: cursor hiding is an editor viewport policy.
        Assert.Null(source.LastCursorVisible);

        source.Mouse = source.Mouse with { Buttons = 0 };
        Input.Poll();
        session.TickLook();
    }

    [Fact]
    public void OrbitHidesTheCursorWhileTheRightButtonIsHeld()
    {
        using var app = new LookTestApp();
        var source = app.Source;

        app.TickLook();
        Assert.Null(source.LastCursorVisible); // nothing pressed: untouched

        source.Mouse = new MouseSnapshot { Position = new Vector2(100, 100), Buttons = RightDown };
        Input.Poll();
        app.TickLook(); // the orbit starts: the cursor is hidden
        Assert.False(source.LastCursorVisible);

        source.Mouse = source.Mouse with { Position = new Vector2(140, 100) };
        Input.Poll();
        app.TickLook(); // still held: still hidden, and the camera rotates
        Assert.False(source.LastCursorVisible);
        Assert.NotEqual(0, app.TestCamera.Yaw);

        source.Mouse = source.Mouse with { Buttons = 0 };
        Input.Poll();
        app.TickLook(); // released: the cursor comes back
        Assert.True(source.LastCursorVisible);
    }

    [Fact]
    public void RefusedLookSessionNeverTouchesTheCursor()
    {
        using var app = new LookTestApp { AllowLook = false };
        var source = app.Source;
        var yawBefore = app.TestCamera.Yaw;

        source.Mouse = new MouseSnapshot { Position = new Vector2(100, 100), Buttons = RightDown };
        Input.Poll();
        app.TickLook();

        Assert.Null(source.LastCursorVisible);
        Assert.Equal(yawBefore, app.TestCamera.Yaw);

        // Released: still nothing to restore.
        source.Mouse = source.Mouse with { Buttons = 0 };
        Input.Poll();
        app.TickLook();
        Assert.Null(source.LastCursorVisible);
    }

    [Fact]
    public void OrbitKeepsRotatingAtTheEdgeAndClipsTheCursorIntoTheLookRect()
    {
        using var app = new LookTestApp { LookClampRect = new UiRect(100, 100, 200, 200) };
        var source = app.Source;
        var yawBefore = app.TestCamera.Yaw;

        // The orbit starts inside the rect: no warp.
        source.Mouse = new MouseSnapshot { Position = new Vector2(150, 150), Buttons = RightDown };
        Input.Poll();
        app.TickLook();
        Assert.False(source.LastCursorVisible);
        Assert.Null(source.LastWarp);

        // The raw movement is counted in full (unlimited orbit as before:
        // 250 px), and the displayed cursor is brought back to the rect edge
        // (300 = X+W, Y+H) instead of overflowing into the UI.
        source.Mouse = source.Mouse with { Position = new Vector2(400, 400) };
        Input.Poll();
        app.TickLook();
        Assert.Equal(250 * 0.003f, app.TestCamera.Yaw - yawBefore, precision: 4);
        Assert.Equal(new Vector2(300, 300), source.LastWarp);

        // The drag continues past the edge: the delta is measured from the
        // retained displayed position (300), so the rotation keeps the same
        // pace and the cursor stays stuck at the edge.
        source.Mouse = source.Mouse with { Position = new Vector2(450, 450) };
        Input.Poll();
        app.TickLook();
        Assert.Equal((250 + 150) * 0.003f, app.TestCamera.Yaw - yawBefore, precision: 4);
        Assert.Equal(new Vector2(300, 300), source.LastWarp);

        // Released: the cursor reappears where it was held, inside the rect.
        source.Mouse = source.Mouse with { Buttons = 0 };
        Input.Poll();
        app.TickLook();
        Assert.True(source.LastCursorVisible);
    }

    [Fact]
    public void OrbitSuppressesUiPointerInputForItsWholeDuration()
    {
        using var app = new LookTestApp();
        var source = app.Source;

        app.TickLook();
        Assert.False(app.TestUi.PointerInputSuppressed);

        source.Mouse = new MouseSnapshot { Position = new Vector2(100, 100), Buttons = RightDown };
        Input.Poll();
        app.TickLook(); // the orbit starts: the UI is put to sleep
        Assert.True(app.TestUi.PointerInputSuppressed);

        source.Mouse = source.Mouse with { Position = new Vector2(140, 100) };
        Input.Poll();
        app.TickLook(); // still held: still asleep
        Assert.True(app.TestUi.PointerInputSuppressed);

        source.Mouse = source.Mouse with { Buttons = 0 };
        Input.Poll();
        app.TickLook(); // released: the UI comes back to life
        Assert.False(app.TestUi.PointerInputSuppressed);
    }

    [Fact]
    public void RefusedLookSessionNeverSuppressesTheUi()
    {
        using var app = new LookTestApp { AllowLook = false };
        var source = app.Source;

        source.Mouse = new MouseSnapshot { Position = new Vector2(100, 100), Buttons = RightDown };
        Input.Poll();
        app.TickLook();
        Assert.False(app.TestUi.PointerInputSuppressed);

        // Released: still nothing to restore or unlock.
        source.Mouse = source.Mouse with { Buttons = 0 };
        Input.Poll();
        app.TickLook();
        Assert.False(app.TestUi.PointerInputSuppressed);
    }

    [Fact]
    public void CursorHidingCanBeDisabled()
    {
        using var app = new LookTestApp { HideCursor = false };
        var source = app.Source;

        source.Mouse = new MouseSnapshot { Position = new Vector2(100, 100), Buttons = RightDown };
        Input.Poll();
        app.TickLook();
        Assert.Null(source.LastCursorVisible); // never hidden

        source.Mouse = source.Mouse with { Buttons = 0 };
        Input.Poll();
        app.TickLook();
        Assert.Null(source.LastCursorVisible); // nor restored (nothing to restore)
    }

    [Fact]
    public void LookRotatesTheCameraInPlaceByDefault()
    {
        using var app = new LookTestApp();
        var source = app.Source;
        var position = app.TestCamera.Position;
        var distance = app.TestCamera.Distance;
        var yawBefore = app.TestCamera.Yaw;

        source.Mouse = new MouseSnapshot { Position = new Vector2(100, 100), Buttons = RightDown };
        Input.Poll();
        app.TickLook(); // starts the look

        source.Mouse = source.Mouse with { Position = new Vector2(160, 100) };
        Input.Poll();
        app.TickLook(); // rotates in place

        Assert.NotEqual(yawBefore, app.TestCamera.Yaw);
        Assert.Equal(position, app.TestCamera.Position); // the position does not move
        Assert.Equal(distance, app.TestCamera.Distance); // the distance is preserved
        // The pivot follows the view axis: no more rotation around a fixed point.
        Assert.Equal(distance, Vector3.Distance(app.TestCamera.Position, app.TestCamera.Pivot), precision: 4);
    }

    [Fact]
    public void OrbitModeStillRotatesAroundThePivot()
    {
        using var app = new LookTestApp { FreeLookEnabled = false };
        var source = app.Source;
        var pivot = app.TestCamera.Pivot;
        var distance = app.TestCamera.Distance;
        var yawBefore = app.TestCamera.Yaw;

        source.Mouse = new MouseSnapshot { Position = new Vector2(100, 100), Buttons = RightDown };
        Input.Poll();
        app.TickLook(); // starts the orbit

        source.Mouse = source.Mouse with { Position = new Vector2(160, 100) };
        Input.Poll();
        app.TickLook(); // rotates around the pivot

        Assert.NotEqual(yawBefore, app.TestCamera.Yaw);
        Assert.Equal(pivot, app.TestCamera.Pivot); // the pivot does not move
        Assert.Equal(distance, app.TestCamera.Distance); // the distance is preserved
        Assert.Equal(distance, Vector3.Distance(app.TestCamera.Position, pivot), precision: 4);
    }

    [Fact]
    public void PanMovesThePivotAndCameraTogether()
    {
        using var app = new LookTestApp();
        var source = app.Source;
        const uint MiddleDown = 1u << (int)MouseButton.Middle;

        var pivotBefore = app.TestCamera.Pivot;
        var positionBefore = app.TestCamera.Position;
        var distance = app.TestCamera.Distance;
        var forward = app.TestCamera.Forward;
        var right = app.TestCamera.Right;
        var up = app.TestCamera.Up;

        source.Mouse = new MouseSnapshot { Position = new Vector2(100, 100), Buttons = MiddleDown };
        Input.Poll();
        app.TickPan(); // starts the pan: not moving yet
        Assert.Equal(pivotBefore, app.TestCamera.Pivot);

        source.Mouse = source.Mouse with { Position = new Vector2(150, 120) };
        Input.Poll();
        app.TickPan(); // drag (dx=50, dy=20)

        // The pivot (and the camera) moved, but distance and orientation remain.
        Assert.NotEqual(pivotBefore, app.TestCamera.Pivot);
        Assert.NotEqual(positionBefore, app.TestCamera.Position);
        Assert.Equal(distance, app.TestCamera.Distance);
        Assert.Equal(forward, app.TestCamera.Forward);
        Assert.Equal(distance, Vector3.Distance(app.TestCamera.Position, app.TestCamera.Pivot), precision: 4);

        // Grab direction: dragging right -> pivot left, dragging down -> pivot
        // up.
        var pivotDelta = app.TestCamera.Pivot - pivotBefore;
        Assert.True(Vector3.Dot(pivotDelta, right) < 0f);
        Assert.True(Vector3.Dot(pivotDelta, up) > 0f);
    }

    [Fact]
    public void PanSuppressesUiPointerInputForItsWholeDuration()
    {
        using var app = new LookTestApp();
        var source = app.Source;
        const uint MiddleDown = 1u << (int)MouseButton.Middle;

        app.TickPan();
        Assert.False(app.TestUi.PointerInputSuppressed);

        source.Mouse = new MouseSnapshot { Position = new Vector2(100, 100), Buttons = MiddleDown };
        Input.Poll();
        app.TickPan(); // the pan starts: the UI is put to sleep
        Assert.True(app.TestUi.PointerInputSuppressed);

        source.Mouse = source.Mouse with { Position = new Vector2(140, 100) };
        Input.Poll();
        app.TickPan(); // still held: still asleep
        Assert.True(app.TestUi.PointerInputSuppressed);

        source.Mouse = source.Mouse with { Buttons = 0 };
        Input.Poll();
        app.TickPan(); // released: the UI comes back to life
        Assert.False(app.TestUi.PointerInputSuppressed);
    }

    [Fact]
    public void RefusedPanNeverMovesOrSuppressesTheUi()
    {
        using var app = new LookTestApp { AllowPan = false };
        var source = app.Source;
        const uint MiddleDown = 1u << (int)MouseButton.Middle;
        var pivotBefore = app.TestCamera.Pivot;

        source.Mouse = new MouseSnapshot { Position = new Vector2(100, 100), Buttons = MiddleDown };
        Input.Poll();
        app.TickPan();
        Assert.False(app.TestUi.PointerInputSuppressed);

        source.Mouse = source.Mouse with { Position = new Vector2(150, 120) };
        Input.Poll();
        app.TickPan();
        Assert.Equal(pivotBefore, app.TestCamera.Pivot);
        Assert.False(app.TestUi.PointerInputSuppressed);

        source.Mouse = source.Mouse with { Buttons = 0 };
        Input.Poll();
        app.TickPan();
        Assert.False(app.TestUi.PointerInputSuppressed);
    }

    [Fact]
    public void WheelZoomsProportionallyToDistanceFromThePivot()
    {
        using var app = new LookTestApp();
        var source = app.Source;
        var pivotBefore = app.TestCamera.Pivot;

        // Near the pivot: small distance step.
        app.TestCamera.Distance = 2f;
        source.Mouse = new MouseSnapshot { Wheel = new Vector2(0, 1) };
        Input.Poll();
        app.TickZoom();
        var nearStep = 2f - app.TestCamera.Distance;

        // Far away: the step grows proportionally to the distance.
        app.TestCamera.Distance = 8f;
        source.Mouse = new MouseSnapshot { Wheel = new Vector2(0, 1) };
        Input.Poll();
        app.TickZoom();
        var farStep = 8f - app.TestCamera.Distance;

        // Same factor per unit of distance, farther = faster, fixed pivot.
        Assert.Equal(nearStep / 2f, farStep / 8f, precision: 4);
        Assert.True(farStep > nearStep);
        Assert.Equal(pivotBefore, app.TestCamera.Pivot);
    }

    [Fact]
    public void WheelDoesNotZoomWhenNotAllowedOrZero()
    {
        using var app = new LookTestApp { AllowZoom = false };
        var source = app.Source;
        var start = app.TestCamera.Position;

        source.Mouse = new MouseSnapshot { Wheel = new Vector2(0, 1) };
        Input.Poll();
        app.TickZoom();
        Assert.Equal(start, app.TestCamera.Position); // forbidden: no zoom

        app.AllowZoom = true;
        source.Mouse = new MouseSnapshot { Wheel = Vector2.Zero };
        Input.Poll();
        app.TickZoom();
        Assert.Equal(start, app.TestCamera.Position); // zero delta: no zoom
    }

    private sealed class GenericLookSession : WindowSession
    {
        public void TickLook() => LookWithMouse();
    }

    private sealed class LookTestApp : Application
    {
        private readonly FakeInputSource _input = new();

        public LookTestApp()
        {
            Input.Bind(_input);
        }

        public FakeInputSource Source => _input;
        public bool AllowLook { get; set; } = true;
        public bool HideCursor { get; set; } = true;
        public bool AllowZoom { get; set; } = true;
        public bool AllowPan { get; set; } = true;
        public UiRect? LookClampRect { get; set; }
        public bool FreeLookEnabled { get; set; } = true;

        protected override IPlatform CreatePlatform() => new FakePlatform(_input);
        protected override bool CanMouseLook() => AllowLook;
        protected override bool HideCursorWhileLooking => HideCursor;
        protected override UiRect? MouseLookClampRect => LookClampRect;
        protected override bool CanZoomCamera() => AllowZoom;
        protected override bool CanPan() => AllowPan;
        protected override bool FreeLook => FreeLookEnabled;

        public Camera TestCamera => Camera;
        public UiSystem TestUi => Ui;
        public void TickLook() => LookWithMouse();
        public void TickZoom() => ZoomWithWheel();
        public void TickPan() => PanWithMouse();
    }

    private sealed class FakePlatform : IPlatform
    {
        private readonly IInputSource _input;

        public FakePlatform(IInputSource input) => _input = input;

        public IWindow CreateWindow(WindowOptions options) => new FakeWindow(_input);
        public void PumpEvents() { }
        public bool TryGetDisplayBounds(int displayIndex, out int x, out int y, out int width, out int height)
        {
            x = y = 0;
            width = 1920;
            height = 1080;
            return true;
        }

        public event Action? QuitRequested;
        public void Dispose() { }
    }

    private sealed class FakeWindow : IWindow
    {
        public FakeWindow(IInputSource input) => Input = input;

        public string Title => "Test";
        public void SetTitle(string title) { }
        public int Width => 640;
        public int Height => 480;
        public int FramebufferWidth => 640;
        public int FramebufferHeight => 480;
        public bool IsClosing => false;
        public bool IsMinimized => false;
        public bool IsVisible { get; private set; } = true;
        public nint NativeHandle => 0;
        public WindowChromeState ChromeState => WindowChromeState.Default;
        public void SetFullscreen(bool fullscreen) { }
        public void SetChromeLayout(WindowChromeLayout? layout) { }
        public void SetPosition(int x, int y) { }
        public void SetVisible(bool visible) => IsVisible = visible;
        public void SetInputFocus() { }
        public IInputSource Input { get; }
        public event Action? Closing;
        public event Action<int, int>? Resized;
        public void Close() { }
        public void Dispose() { }
    }
}
