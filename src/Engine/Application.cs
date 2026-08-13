using System.Numerics;
using Crowbar.Engine.InputSystem;
using Crowbar.Engine.Platform;
using Crowbar.Engine.Rendering;
using Crowbar.UI;

namespace Crowbar.Engine;

/// <summary>
/// Runtime loop. Owns the platform window, the UI runtime, the global input
/// facades and the graphics device, and runs every frame in a fixed phase
/// order: <b>input</b> (poll) → <b>update</b> (simulation + UI animations) →
/// <b>render</b> (scene + UI composite). Subclasses wire their content in
/// <see cref="OnInitialize"/> and their logic in <see cref="OnUpdate"/>.
/// </summary>
public abstract class Application : IDisposable
{
    private readonly IPlatform _platform;
    private readonly IWindow _window;
    private readonly UiSystem _ui = new();
    private readonly Camera _camera = new();
    private readonly World _world = new();
    private IGraphicsDevice? _graphics;
    private Renderer? _renderer;
    private bool _looking;
    private float _lastLookX;
    private float _lastLookY;
    private bool _disposed;

    protected Application()
    {
        _platform = CreatePlatform();
        _window = _platform.CreateWindow(CreateWindowOptions());
        BindDefaultActions();
    }

    /// <summary>Platform factory; SDL on desktop.</summary>
    protected virtual IPlatform CreatePlatform() => new SdlPlatform();

    protected virtual WindowOptions CreateWindowOptions() => new(Title: "Crowbar", Width: 1280, Height: 720);

    protected IPlatform Platform => _platform;
    protected IWindow Window => _window;
    protected UiSystem Ui => _ui;
    protected IGraphicsDevice? Graphics => _graphics;
    protected Camera Camera => _camera;
    protected IInputSource InputSource => _window.Input;

    /// <summary>The viewport renderer (null until the window is loaded, or headless).</summary>
    protected Renderer? Renderer => _renderer;

    /// <summary>The world owned by the application. Subclasses spawn entities and call Start/Stop on it.</summary>
    protected World World => _world;

    public void Run()
    {
        _window.Loaded += OnWindowLoaded;
        _window.Updating += OnFrame;
        _window.Rendering += RenderFrame;
        _window.Resized += OnResized;
        _window.Closing += OnWindowClosing;
        _window.Run();
        _window.Dispose();
        _platform.Dispose();
    }

    private void OnWindowLoaded()
    {
        _graphics = CreateGraphicsDevice(_window);
        if (_graphics is not null)
        {
            _renderer = new Renderer(_graphics);
            var width = FramebufferWidth;
            var height = FramebufferHeight;
            _ui.SetViewport(width, height);
            // Layout only: the GPU renderer paints the tree (no Skia raster).
            _ui.Prepare();
        }
        WireUiInput();
        OnInitialize();
    }

    /// <summary>
    /// Creates the graphics device for the window. Returns null to run headless
    /// (no surface); subclasses may override for test or alternate backends.
    /// </summary>
    protected virtual IGraphicsDevice? CreateGraphicsDevice(IWindow window) =>
        new WebGpuContext(window.NativeHandle, FramebufferWidth, FramebufferHeight);

    /// <summary>Forwards the window's raw input events into the UI runtime.</summary>
    private void WireUiInput()
    {
        var input = _window.Input;
        input.PointerMoved += e =>
        {
            Ui.ProcessPointerMove(e.X, e.Y);
            ApplyUiCursor();
        };
        input.PointerButtonChanged += e =>
        {
            if (e.IsDown) Ui.ProcessPointerDown(e.X, e.Y, (int)e.Button);
            else Ui.ProcessPointerUp(e.X, e.Y, (int)e.Button);
        };
        input.PointerWheelChanged += e => Ui.ProcessPointerWheel(e.X, e.Y, e.DeltaX, e.DeltaY);
        input.KeyChanged += Ui.ProcessKey;
    }

    /// <summary>Frame phase order: poll input first, then update simulation, then the world, then UI.</summary>
    private void OnFrame(double delta)
    {
        Input.Poll();
        var clamped = Math.Clamp((float)delta, 0f, 0.1f);
        OnUpdate(clamped);
        World.Update(clamped);
        Ui.Update(clamped);
        // The hover cursor can change without a pointer move (a re-render, a
        // scroll under a stationary cursor): re-assert it every frame. The
        // platform skips the SDL call when the shape is unchanged.
        ApplyUiCursor();
    }

    /// <summary>Pushes the UI's resolved hover cursor to the OS cursor.</summary>
    private void ApplyUiCursor() => InputSource.SetCursorShape(Ui.HoveredCursor);

    private void RenderFrame(double delta)
    {
        OnRender((float)delta);
        _renderer?.Render(World, _camera, delta, _ui);
    }

    private void OnResized(int width, int height)
    {
        _renderer?.Resize(width, height);
        Ui.SetViewport(width, height);
        Ui.Prepare();
        OnResize(width, height);
    }

    private void OnWindowClosing()
    {
        OnClosing();
        World.Dispose();
        _renderer?.Dispose();
        _renderer = null;
        Graphics?.Dispose();
        _graphics = null;
    }

    /// <summary>
    /// Default camera controller: right-drag mouse look (the cursor is never
    /// warped to the center) and ZQSD/space movement via the bound actions.
    /// </summary>
    protected virtual void UpdateCameraControls(float delta)
    {
        if (!Input.IsWindowFocused)
            return;

        LookWithMouse();

        var movement = Vector3.Zero;
        if (Input.IsPressed("forward")) movement += Camera.Forward;
        if (Input.IsPressed("backward")) movement -= Camera.Forward;
        if (Input.IsPressed("right")) movement += Camera.Right;
        if (Input.IsPressed("left")) movement -= Camera.Right;
        if (Input.IsPressed("up")) movement += Vector3.UnitY;
        if (Input.IsPressed("down")) movement -= Vector3.UnitY;

        if (movement.LengthSquared() > 0f)
            Camera.Position += Vector3.Normalize(movement) * (2.5f * delta);
    }

    /// <summary>
    /// Rotates the camera from the real cursor movement while the right button
    /// is held; the delta is measured between the previous and the current
    /// cursor position, so the cursor moves naturally instead of being locked.
    /// </summary>
    protected virtual void LookWithMouse()
    {
        if (!Mouse.IsDown(MouseButton.Right))
        {
            _looking = false;
            return;
        }

        var position = Mouse.Position;
        if (!_looking)
        {
            _lastLookX = position.X;
            _lastLookY = position.Y;
            _looking = true;
            return;
        }

        var deltaX = position.X - _lastLookX;
        var deltaY = position.Y - _lastLookY;
        _lastLookX = position.X;
        _lastLookY = position.Y;
        Camera.Yaw += deltaX * 0.003f;
        Camera.Pitch = Math.Clamp(Camera.Pitch - deltaY * 0.003f, -1.45f, 1.45f);
    }

    /// <summary>
    /// Binds the default movement actions to the keys producing Z, Q, S, D
    /// (AZERTY) / W, A, S, D (QWERTY) in the active layout, plus space/E. The
    /// layout is resolved through the platform, so the bindings follow the
    /// labels the user sees instead of fixed physical positions.
    /// </summary>
    protected virtual void BindDefaultActions()
    {
        Input.BindAction("forward", InputSource.KeyForChar('z'));
        Input.BindAction("backward", InputSource.KeyForChar('s'));
        Input.BindAction("right", InputSource.KeyForChar('d'));
        Input.BindAction("left", InputSource.KeyForChar('q'));
        Input.BindAction("up", Key.Space);
        Input.BindAction("down", InputSource.KeyForChar('e'));
    }

    protected virtual void OnInitialize() { }

    protected virtual void OnUpdate(float deltaTime) => UpdateCameraControls(deltaTime);

    protected virtual void OnRender(float deltaTime) { }

    protected virtual void OnResize(int width, int height) { }

    protected virtual void OnClosing() { }

    private int FramebufferWidth =>
        Math.Max(1, Window.FramebufferWidth > 0 ? Window.FramebufferWidth : Window.Width);

    private int FramebufferHeight =>
        Math.Max(1, Window.FramebufferHeight > 0 ? Window.FramebufferHeight : Window.Height);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Ui.Dispose();
        World.Dispose();
        _renderer?.Dispose();
        _renderer = null;
        Graphics?.Dispose();
        _graphics = null;
        _window.Dispose();
        _platform.Dispose();
    }
}
