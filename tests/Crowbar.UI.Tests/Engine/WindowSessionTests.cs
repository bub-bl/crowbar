using Crowbar.Engine.InputSystem;
using Crowbar.Engine.InputSystem.Tests;
using Crowbar.Engine.Platform;
using Crowbar.Engine.Rendering;
using Crowbar.UI;
using Xunit;

namespace Crowbar.Engine.Tests;

/// <summary>
/// Multi-window system tests: the per-window <see cref="WindowSession"/> unit
/// (own UI runtime, initialize-once lifecycle, close → teardown) and the
/// <see cref="Application"/> host loop (quit on platform quit, quit when the
/// primary window closes, extra windows with their own session). All run
/// headless with a fake platform/window and no graphics device. The host
/// binds the static <see cref="Input"/> facade (focus routing), so these
/// tests join the <c>InputFacade</c> collection.
/// </summary>
[Collection("InputFacade")]
public class WindowSessionTests
{
    [Fact]
    public void Session_InitializesExactlyOnce()
    {
        var window = new FakeWindow();
        using var session = new CountingSession(window, null);

        session.Initialize();
        session.Initialize();
        Assert.Equal(1, session.InitializeCount);
    }

    [Fact]
    public void Session_ClosingDisposesTheWindowAndRaisesClosed()
    {
        var window = new FakeWindow();
        var session = new WindowSession(window, null);
        var closed = false;
        session.Closed += _ => closed = true;

        window.Close();

        Assert.True(closed);
        Assert.True(window.Disposed);
    }

    [Fact]
    public void Session_IsFocusedFollowsItsInputSource()
    {
        var window = new FakeWindow();
        using var session = new WindowSession(window, null);
        var source = (FakeInputSource)window.Input;

        Assert.True(session.IsFocused);
        source.IsWindowFocused = false;
        Assert.False(session.IsFocused);
    }

    [Fact]
    public void Session_UpdateAndRenderRunHeadless()
    {
        var window = new FakeWindow();
        using var session = new WindowSession(window, null);
        session.World = new World();

        // No graphics device: chrome sync, camera controls and the UI update
        // must degrade gracefully instead of throwing.
        session.Update(1f / 60f);
        session.Render(1f / 60f);
        Assert.False(window.Disposed);
    }

    [Fact]
    public void Host_RunQuitsWhenThePlatformRequestsAQuit()
    {
        var app = new HostTestApp();
        app.TestPlatform.RaiseQuitOnPump = 1;

        app.Run(); // must return, not hang

        Assert.Equal(1, app.InitializeCount);
        Assert.True(app.TestPlatform.Disposed);
    }

    [Fact]
    public void Host_RunQuitsWhenThePrimaryWindowCloses()
    {
        var app = new HostTestApp();
        app.TestPlatform.ClosePrimaryOnPump = 1;

        app.Run();

        Assert.True(app.TestPlatform.Disposed);
        Assert.True(app.TestPlatform.Windows[0].IsClosing);
    }

    [Fact]
    public void Host_OpenWindowCreatesASecondSessionWithItsOwnUi()
    {
        var app = new HostTestApp();
        try
        {
            var extra = app.OpenExtra();
            Assert.Equal(2, app.TestPlatform.Windows.Count);
            Assert.NotSame(app.Ui, extra.Ui); // every window has its own Razor UI runtime

            var closed = false;
            extra.Closed += _ => closed = true;
            extra.Window.Close();
            Assert.True(closed);
            Assert.True(((FakeWindow)extra.Window).Disposed);
        }
        finally
        {
            app.Dispose();
        }
    }

    [Fact]
    public void Host_ClosingThePrimaryWindowClosesExtraWindows()
    {
        var app = new HostTestApp();
        try
        {
            var extra = app.OpenExtra();
            var extraWindow = (FakeWindow)extra.Window;

            app.Window.Close();

            Assert.True(extraWindow.IsClosing);
        }
        finally
        {
            app.Dispose();
        }
    }

    [Fact]
    public void Host_ClosingAnExtraWindowKeepsThePrimaryAlive()
    {
        var app = new HostTestApp();
        try
        {
            var extra = app.OpenExtra();
            extra.Window.Close();
            Assert.False(app.Window.IsClosing);
        }
        finally
        {
            app.Dispose();
        }
    }

    [Fact]
    public void Session_HideOnCloseHidesInsteadOfDisposing()
    {
        var window = new FakeWindow();
        using var session = new HidingSession(window, null);
        var closed = false;
        session.Closed += _ => closed = true;

        window.Close(); // like the OS close button on the notification window

        // The session stays alive: the window is completely hidden, never
        // destroyed, and the host is not told to prune it.
        Assert.False(window.IsVisible);
        Assert.False(window.Disposed);
        Assert.False(closed);
        Assert.False(session.Disposed);
    }

    [Fact]
    public void Session_RenderSkipsHiddenWindows()
    {
        var window = new FakeWindow();
        using var session = new WindowSession(window, null);
        session.World = new World();
        window.SetVisible(false);

        session.Update(1f / 60f);
        session.Render(1f / 60f); // must not throw (no GPU work for hidden windows)
        Assert.False(window.Disposed);
    }

    [Fact]
    public void Host_ClosingAnExtraWindowMidLoopDoesNotCrashTheFrame()
    {
        var app = new HostTestApp();
        var extra = app.OpenExtra();

        // Pump 1 closes the extra window inside the event pump (like a native
        // close event); pump 2 quits. The loop must not run a frame on the
        // torn-down session.
        app.TestPlatform.CloseWindowOnPump = 1;
        app.TestPlatform.CloseWindowIndex = 1;
        app.TestPlatform.RaiseQuitOnPump = 2;

        app.Run();

        Assert.True(((FakeWindow)extra.Window).Disposed);
        // The platform quit closes every window (primary included).
        Assert.True(app.Window.IsClosing);
        Assert.True(app.TestPlatform.Disposed);
    }

    private sealed class CountingSession : WindowSession
    {
        public CountingSession(IWindow window, IGraphicsDevice? graphics) : base(window, graphics) { }

        public int InitializeCount { get; private set; }

        protected override void OnInitialize() => InitializeCount++;
    }

    private sealed class HidingSession : WindowSession
    {
        public HidingSession(IWindow window, IGraphicsDevice? graphics) : base(window, graphics) { }

        public bool Disposed { get; private set; }

        protected override bool HideOnClose => true;

        public override void Dispose()
        {
            Disposed = true;
            base.Dispose();
        }
    }

    private sealed class HostTestApp : Application
    {
        private readonly FakePlatform _platform = new();

        public HostTestApp()
        {
        }

        public FakePlatform TestPlatform => _platform;
        public int InitializeCount { get; private set; }

        protected override IPlatform CreatePlatform() => _platform;

        // Silent engine in tests: no SDL audio device is opened.
        protected override Audio.IAudioBackend? CreateAudioBackend() => null;

        protected override void OnInitialize() => InitializeCount++;

        public WindowSession OpenExtra() => OpenWindow(new WindowOptions(Title: "Extra", Width: 320, Height: 200));
    }

    private sealed class FakePlatform : IPlatform
    {
        public List<FakeWindow> Windows { get; } = [];
        public int PumpCount { get; private set; }

        /// <summary>Pump number on which to raise QuitRequested (0 = never).</summary>
        public int RaiseQuitOnPump { get; set; }

        /// <summary>Pump number on which to close the first window (0 = never).</summary>
        public int ClosePrimaryOnPump { get; set; }

        /// <summary>Pump number on which to close <see cref="CloseWindowIndex"/> (0 = never).</summary>
        public int CloseWindowOnPump { get; set; }

        /// <summary>Index of the window closed by <see cref="CloseWindowOnPump"/>.</summary>
        public int CloseWindowIndex { get; set; }

        public bool Disposed { get; private set; }

        public IWindow CreateWindow(WindowOptions options)
        {
            var window = new FakeWindow { Title = options.Title };
            Windows.Add(window);
            return window;
        }

        public bool TryGetDisplayBounds(int displayIndex, out int x, out int y, out int width, out int height)
        {
            x = y = 0;
            width = 1920;
            height = 1080;
            return true;
        }

        public void PumpEvents()
        {
            PumpCount++;
            if (RaiseQuitOnPump == PumpCount)
                QuitRequested?.Invoke();
            if (ClosePrimaryOnPump == PumpCount && Windows.Count > 0)
                Windows[0].Close();
            if (CloseWindowOnPump == PumpCount && CloseWindowIndex < Windows.Count)
                Windows[CloseWindowIndex].Close();
        }

        public event Action? QuitRequested;

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeWindow : IWindow
    {
        public FakeWindow()
        {
            Input = new FakeInputSource();
        }

        public string Title { get; set; } = "Test";
        public void SetTitle(string title) => Title = title;
        public int Width => 640;
        public int Height => 480;
        public int FramebufferWidth => 640;
        public int FramebufferHeight => 480;
        public bool IsClosing { get; private set; }
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
        public bool Disposed { get; private set; }

        public event Action? Closing;
        public event Action<int, int>? Resized;

        public void Close()
        {
            if (IsClosing)
                return;
            IsClosing = true;
            Closing?.Invoke();
        }

        public void Dispose() => Disposed = true;
    }
}
