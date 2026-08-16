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
    private bool _lookAllowed;
    private bool _cursorHidden;
    private IDisposable? _pointerModal;
    private bool _panning;
    private bool _panAllowed;
    private IDisposable? _panModal;
    private float _lastLookX;
    private float _lastLookY;
    private float _lastPanX;
    private float _lastPanY;
    private bool _hasWindowChromeState;
    private WindowChromeState _lastWindowChromeState;
    private bool _lastWindowMinimized;
    private int _chromeRefreshFrames;
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
        UpdateWindowChrome();
        World.Update(clamped);
        Ui.Update(clamped);
        // The hover cursor can change without a pointer move (a re-render, a
        // scroll under a stationary cursor): re-assert it every frame. The
        // platform skips the SDL call when the shape is unchanged.
        ApplyUiCursor();
    }

    /// <summary>
    /// Mirrors the platform's window-chrome state into the UI, pushes the UI's
    /// title-bar geometry back to the platform window, and applies the global
    /// window shortcut: F11 toggles exclusive (game-style) fullscreen.
    /// </summary>
    private void UpdateWindowChrome()
    {
        var state = _window.ChromeState;
        var restoredFromMinimize = _lastWindowMinimized && !_window.IsMinimized;
        var nativeStateChanged = _hasWindowChromeState &&
            (state.IsActive != _lastWindowChromeState.IsActive ||
             state.IsMaximized != _lastWindowChromeState.IsMaximized ||
             state.IsFullscreen != _lastWindowChromeState.IsFullscreen);

        Ui.ChromeState = state;
        if (restoredFromMinimize || nativeStateChanged)
            // Native restore/maximize transitions can span several SDL/Win32
            // messages. Keep repainting for a short settling window instead of
            // relying on the next mouse move to become the first invalidation.
            _chromeRefreshFrames = Math.Max(_chromeRefreshFrames, 8);

        if (_chromeRefreshFrames > 0)
        {
            // A restore can leave the GPU UI target visually stale even though
            // the main loop is running. Mouse movement over the client titlebar
            // used to be the accidental first invalidation, which made the
            // native caption buttons appear to "unlock" only after hovering.
            // Invalidate layout/paint deterministically for the whole native
            // transition, not just the first frame.
            Ui.Screen.Invalidate();
            Ui.Renderer.MarkDirty();
            _chromeRefreshFrames--;
        }

        _window.SetChromeLayout(Ui.WindowChrome);
        _lastWindowChromeState = state;
        _lastWindowMinimized = _window.IsMinimized;
        _hasWindowChromeState = true;

        // F11 is a window-level shortcut: it must not fire while the user is
        // typing in a text field (the UI owns the keyboard then).
        if (Input.WasPressed(Key.F11) && !Ui.KeyboardConsumed)
            _window.SetFullscreen(!state.IsFullscreen);
    }

    /// <summary>Pushes the UI's resolved hover cursor to the OS cursor.</summary>
    private void ApplyUiCursor() => InputSource.SetCursorShape(Ui.HoveredCursor);

    private void RenderFrame(double delta)
    {
        // SDL can keep the main loop alive while Windows has detached the
        // minimized window from its swapchain. Do not submit GPU work in that
        // state; SdlWindow will refresh the surface and emit Resized when the
        // taskbar restores it.
        if (_window.IsMinimized)
            return;

        OnRender((float)delta);
        _renderer?.Render(World, _camera, delta, _ui);

        // Renderer.Render runs Ui.Prepare(), which is the moment Yoga has
        // resolved the current title-bar rectangles. Push that freshly laid-out
        // geometry immediately; doing it only in OnFrame would leave the
        // platform hit-test one render behind and could leave the buttons with
        // an empty layout after the first page build or a resize.
        _window.SetChromeLayout(Ui.WindowChrome);
    }

    private void OnResized(int width, int height)
    {
        _renderer?.Resize(width, height);
        // Layout + repaint happen in the render loop (Renderer.Render calls
        // Ui.Prepare and repaints when it returns true). Preparing here would
        // consume the dirty flag first, so the render loop would skip the
        // repaint and the UI offscreen target would stay at the old size.
        Ui.SetViewport(width, height);
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
    /// Default camera controller: right-drag looks around (a free camera by
    /// default — the camera rotates in place instead of orbiting a fixed
    /// point — with the cursor hidden and clamped, never warped to the
    /// center), middle-drag pans, the wheel dollies along the view axis, and
    /// ZQSD/space translates the rig via the bound actions.
    /// </summary>
    protected virtual void UpdateCameraControls(float delta)
    {
        if (!Input.IsWindowFocused)
        {
            // The window loses focus mid-orbit: the button may never come
            // back up on the OS side, so we also restore the cursor here so it
            // is not left hidden indefinitely.
            RestoreCursorIfHidden();
            return;
        }

        LookWithMouse();
        PanWithMouse();
        ZoomWithWheel();

        var movement = Vector3.Zero;
        if (CanMoveCamera())
        {
            if (Input.IsPressed("forward")) movement += Camera.Forward;
            if (Input.IsPressed("backward")) movement -= Camera.Forward;
            if (Input.IsPressed("right")) movement += Camera.Right;
            if (Input.IsPressed("left")) movement -= Camera.Right;
            if (Input.IsPressed("up")) movement += Vector3.UnitY;
            if (Input.IsPressed("down")) movement -= Vector3.UnitY;
        }

        // ZQSD/space moves the pivot (and therefore the camera with it): this
        // is a translation of the orbit rig, the pivot distance is preserved.
        if (movement.LengthSquared() > 0f)
        {
            Camera.Pivot += Vector3.Normalize(movement) * (2.5f * delta);
            SyncOrbitPosition();
        }
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
            ReleasePointerModal();
            RestoreCursorIfHidden();
            return;
        }

        var position = Mouse.Position;
        if (!_looking)
        {
            _looking = true;
            // The authorization is decided at the press: a drag started on
            // the UI (or outside the viewport) never begins orbiting, even
            // if the cursor then enters the viewport; conversely, a drag
            // engaged inside the viewport continues over the panels.
            _lookAllowed = CanMouseLook();
            if (!_lookAllowed)
                return;
            // The orbit hides the OS cursor for the duration of the drag.
            if (HideCursorWhileLooking && !_cursorHidden)
            {
                Mouse.SetCursorVisible(false);
                _cursorHidden = true;
            }
            // The UI is put to sleep for the whole session: no more hover,
            // tooltip, cursor or click can reach the panels, even those
            // floating inside the viewport (toolbar, tabs).
            _pointerModal = Ui.EnterModal();
            // Drag reference point: confined to the rect (normally a no-op,
            // an authorized press is already inside the viewport).
            position = ConfineLookCursor(position);
            _lastLookX = position.X;
            _lastLookY = position.Y;
            return;
        }

        if (!_lookAllowed)
            return;

        // The delta is measured on the raw mouse movement, as before: the
        // orbit stays unlimited even when the cursor reaches the viewport
        // edge. Then the displayed position (last + delta) is confined to the
        // rect: the OS cursor is brought back into the viewport if it would
        // leave it, without ever truncating the orbit delta.
        var deltaX = position.X - _lastLookX;
        var deltaY = position.Y - _lastLookY;
        Camera.Yaw += deltaX * 0.003f;
        Camera.Pitch = Math.Clamp(Camera.Pitch - deltaY * 0.003f, -1.45f, 1.45f);
        // The orientation changed: in freemode the camera rotates in place
        // (its position stays fixed, the pivot follows the view axis); in
        // orbit mode it moves back onto the sphere around the fixed pivot.
        // The distance is preserved in both cases.
        if (FreeLook)
            SyncFreeLookPivot();
        else
            SyncOrbitPosition();
        position = ConfineLookCursor(new Vector2(_lastLookX + deltaX, _lastLookY + deltaY));
        _lastLookX = position.X;
        _lastLookY = position.Y;
    }

    /// <summary>
    /// Translates the camera rig (pivot and camera together) from a
    /// middle-button drag, so the scene follows the cursor. The step scales
    /// with the view distance, matching the zoom feel. The cursor stays visible
    /// (no clamp), but the UI is frozen for the duration of the drag.
    /// </summary>
    protected virtual void PanWithMouse()
    {
        if (!Mouse.IsDown(MouseButton.Middle))
        {
            _panning = false;
            ReleasePanModal();
            return;
        }

        var position = Mouse.Position;
        if (!_panning)
        {
            _panning = true;
            // Like the orbit: the decision is made at the press.
            _panAllowed = CanPan();
            if (!_panAllowed)
                return;
            _panModal = Ui.EnterModal();
            _lastPanX = position.X;
            _lastPanY = position.Y;
            return;
        }

        if (!_panAllowed)
            return;

        var deltaX = position.X - _lastPanX;
        var deltaY = position.Y - _lastPanY;
        // Grabbing the scene: dragging right moves the pivot left, dragging
        // down moves it up.
        var scale = Camera.Distance * PanSpeed;
        Camera.Pivot += (-Camera.Right * deltaX + Camera.Up * deltaY) * scale;
        SyncOrbitPosition();
        _lastPanX = position.X;
        _lastPanY = position.Y;
    }

    /// <summary>
    /// Puts the camera back on its orbit sphere:
    /// <c>Position = Pivot - Forward * Distance</c>. Called after every
    /// change of orientation, pivot or distance.
    /// </summary>
    private void SyncOrbitPosition() =>
        Camera.Position = Camera.Pivot - Camera.Forward * Camera.Distance;

    /// <summary>
    /// Free camera: the rotation happens around the camera itself (its
    /// position stays fixed), so the pivot is recomputed on the view axis at
    /// the current distance (<c>Pivot = Position + Forward * Distance</c>).
    /// The invariant <c>Position == Pivot - Forward * Distance</c> still
    /// holds, which keeps pan/zoom/dolly coherent with the orbit mode.
    /// </summary>
    private void SyncFreeLookPivot() =>
        Camera.Pivot = Camera.Position + Camera.Forward * Camera.Distance;

    /// <summary>Restores the OS cursor to the UI if it was hidden by the orbit.</summary>
    private void RestoreCursorIfHidden()
    {
        if (!_cursorHidden)
            return;
        Mouse.SetCursorVisible(true);
        _cursorHidden = false;
    }

    /// <summary>Closes the pointer modal session opened by the orbit, if any.</summary>
    private void ReleasePointerModal()
    {
        _pointerModal?.Dispose();
        _pointerModal = null;
    }

    /// <summary>Closes the pointer modal session opened by the pan, if any.</summary>
    private void ReleasePanModal()
    {
        _panModal?.Dispose();
        _panModal = null;
    }

    /// <summary>
    /// Confines the displayed cursor position into
    /// <see cref="MouseLookClampRect"/>: if it would leave it, the cursor is
    /// brought back to the edge on the OS side (warp) and the returned
    /// position serves as the reference for the next delta. The orbit delta,
    /// on the other hand, is measured on the raw movement before this
    /// confinement: pushing against an edge keeps turning while the cursor
    /// stays stuck inside the viewport.
    /// </summary>
    private Vector2 ConfineLookCursor(Vector2 position)
    {
        if (MouseLookClampRect is not { } rect)
            return position;

        var clampedX = Math.Clamp(position.X, rect.X, rect.Right);
        var clampedY = Math.Clamp(position.Y, rect.Y, rect.Bottom);
        if (clampedX != position.X || clampedY != position.Y)
            Mouse.SetCursorAt(clampedX, clampedY);
        return new Vector2(clampedX, clampedY);
    }

    /// <summary>
    /// True when the right-drag look rotates the camera around its own position
    /// (free camera) instead of orbiting the fixed <see cref="Camera.Pivot"/>.
    /// Defaults to true; override to false for the orbit-around-a-pivot
    /// behavior. Pan/zoom/dolly stay coherent in both modes.
    /// </summary>
    protected virtual bool FreeLook => true;

    /// <summary>
    /// True when a new right-drag mouse-look session may start. Evaluated once
    /// at the moment the right button goes down and latched for the whole
    /// session. Subclasses confine the look to their viewport: e.g. only when
    /// the press began inside the docked scene viewport and the UI did not
    /// consume it (toolbar, tab, input, scrollbar, overlay).
    /// </summary>
    protected virtual bool CanMouseLook() => true;

    /// <summary>
    /// True when the OS cursor is hidden for the duration of a mouse-look
    /// session (it is restored as soon as the right button is released).
    /// </summary>
    protected virtual bool HideCursorWhileLooking => true;

    /// <summary>
    /// Rectangle (window pixels, origin top-left) in which the cursor is
    /// confined for the duration of an orbit session, or null to leave it
    /// free. The editor bounds it to the docked viewport: the mouse cannot
    /// leave the scene during a drag, even hidden.
    /// </summary>
    protected virtual UiRect? MouseLookClampRect => null;

    /// <summary>
    /// True when the bound movement keys may move the camera. Subclasses
    /// disable it while the UI is consuming the keyboard (a focused text
    /// input must not fight the camera for the same keystrokes).
    /// </summary>
    protected virtual bool CanMoveCamera() => true;

    /// <summary>
    /// True when a new middle-drag pan may start. Evaluated once at the press
    /// and latched for the session, mirroring <see cref="CanMouseLook"/>.
    /// Subclasses confine it to their viewport and off the UI.
    /// </summary>
    protected virtual bool CanPan() => true;

    private const float MinZoomDistance = 0.5f;

    /// <summary>
    /// Dollies the camera along its view axis from the mouse wheel. The step is
    /// proportional to the view distance (multiplicative), so the zoom feels
    /// consistent near and far and never crosses the pivot.
    /// </summary>
    protected virtual void ZoomWithWheel()
    {
        var wheel = Mouse.Wheel.Y;
        if (wheel == 0f || !CanZoomCamera())
            return;
        Camera.Distance = Math.Max(MinZoomDistance, Camera.Distance * (1f - wheel * ZoomSpeed));
        SyncOrbitPosition();
    }

    /// <summary>Fraction of the orbit distance dollied per wheel notch.</summary>
    protected virtual float ZoomSpeed => 0.1f;

    /// <summary>World units per pixel per unit of orbit distance (≈1:1 grab at 60° FOV).</summary>
    protected virtual float PanSpeed => 0.002f;

    /// <summary>
    /// True when the mouse wheel may zoom the camera. Subclasses confine it to
    /// their viewport: only when the cursor is over the scene and not over UI
    /// (a scrollable panel or an interactive element owns the wheel).
    /// </summary>
    protected virtual bool CanZoomCamera() => true;

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
        ReleasePointerModal();
        ReleasePanModal();
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
