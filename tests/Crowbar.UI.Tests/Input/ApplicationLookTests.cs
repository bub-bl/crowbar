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
    public void OrbitHidesTheCursorWhileTheRightButtonIsHeld()
    {
        using var app = new LookTestApp();
        var source = app.Source;

        app.TickLook();
        Assert.Null(source.LastCursorVisible); // rien de pressé : pas touché

        source.Mouse = new MouseSnapshot { Position = new Vector2(100, 100), Buttons = RightDown };
        Input.Poll();
        app.TickLook(); // l'orbite démarre : le curseur est masqué
        Assert.False(source.LastCursorVisible);

        source.Mouse = source.Mouse with { Position = new Vector2(140, 100) };
        Input.Poll();
        app.TickLook(); // toujours maintenu : toujours masqué, et la caméra tourne
        Assert.False(source.LastCursorVisible);
        Assert.NotEqual(0, app.TestCamera.Yaw);

        source.Mouse = source.Mouse with { Buttons = 0 };
        Input.Poll();
        app.TickLook(); // relâché : le curseur revient
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

        // Relâché : toujours rien à restaurer.
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

        // L'orbite démarre à l'intérieur du rect : pas de warp.
        source.Mouse = new MouseSnapshot { Position = new Vector2(150, 150), Buttons = RightDown };
        Input.Poll();
        app.TickLook();
        Assert.False(source.LastCursorVisible);
        Assert.Null(source.LastWarp);

        // Le mouvement brut est compté en entier (orbite illimitée comme
        // avant : 250 px), et le curseur affiché est ramené au bord du rect
        // (300 = X+W, Y+H) au lieu de déborder dans l'UI.
        source.Mouse = source.Mouse with { Position = new Vector2(400, 400) };
        Input.Poll();
        app.TickLook();
        Assert.Equal(250 * 0.003f, app.TestCamera.Yaw - yawBefore, precision: 4);
        Assert.Equal(new Vector2(300, 300), source.LastWarp);

        // Le drag continue après le bord : le delta est mesuré depuis la
        // position affichée retenue (300), donc la rotation poursuit au même
        // rythme et le curseur reste coincé au bord.
        source.Mouse = source.Mouse with { Position = new Vector2(450, 450) };
        Input.Poll();
        app.TickLook();
        Assert.Equal((250 + 150) * 0.003f, app.TestCamera.Yaw - yawBefore, precision: 4);
        Assert.Equal(new Vector2(300, 300), source.LastWarp);

        // Relâché : le curseur réapparaît là où il a été retenu, dans le rect.
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
        app.TickLook(); // l'orbite démarre : l'UI est mise en veille
        Assert.True(app.TestUi.PointerInputSuppressed);

        source.Mouse = source.Mouse with { Position = new Vector2(140, 100) };
        Input.Poll();
        app.TickLook(); // toujours maintenu : toujours en veille
        Assert.True(app.TestUi.PointerInputSuppressed);

        source.Mouse = source.Mouse with { Buttons = 0 };
        Input.Poll();
        app.TickLook(); // relâché : l'UI revient à la vie
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

        // Relâché : toujours rien à restaurer ni à débloquer.
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
        Assert.Null(source.LastCursorVisible); // jamais masqué

        source.Mouse = source.Mouse with { Buttons = 0 };
        Input.Poll();
        app.TickLook();
        Assert.Null(source.LastCursorVisible); // ni restauré (rien à restaurer)
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
        public UiRect? LookClampRect { get; set; }

        protected override IPlatform CreatePlatform() => new FakePlatform(_input);
        protected override bool CanMouseLook() => AllowLook;
        protected override bool HideCursorWhileLooking => HideCursor;
        protected override UiRect? MouseLookClampRect => LookClampRect;

        public Camera TestCamera => Camera;
        public UiSystem TestUi => Ui;
        public void TickLook() => LookWithMouse();
    }

    private sealed class FakePlatform : IPlatform
    {
        private readonly IInputSource _input;

        public FakePlatform(IInputSource input) => _input = input;

        public IWindow CreateWindow(WindowOptions options) => new FakeWindow(_input);
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
        public nint NativeHandle => 0;
        public IInputSource Input { get; }
        public event Action? Loaded;
        public event Action? Closing;
        public event Action<double>? Updating;
        public event Action<double>? Rendering;
        public event Action<int, int>? Resized;
        public void Run() { }
        public void Close() { }
        public void Dispose() { }
    }
}
