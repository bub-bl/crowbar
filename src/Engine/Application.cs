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
        {
            // La fenêtre perd le focus en plein orbite : le bouton peut ne plus
            // jamais remonter côté OS, donc on rend le curseur ici aussi pour
            // ne pas le laisser masqué indéfiniment.
            RestoreCursorIfHidden();
            return;
        }

        LookWithMouse();

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
            Ui.PointerInputSuppressed = false;
            RestoreCursorIfHidden();
            return;
        }

        var position = Mouse.Position;
        if (!_looking)
        {
            _looking = true;
            // L'autorisation est tranchée à l'appui : un drag commencé sur
            // l'UI (ou hors du viewport) ne commence jamais à orbiter, même
            // si le curseur entre ensuite dans le viewport ; à l'inverse un
            // drag engagé dans le viewport continue au-dessus des panneaux.
            _lookAllowed = CanMouseLook();
            if (!_lookAllowed)
                return;
            // L'orbite cache le curseur OS pour la durée du drag.
            if (HideCursorWhileLooking && !_cursorHidden)
            {
                Mouse.SetCursorVisible(false);
                _cursorHidden = true;
            }
            // L'UI est mise en veille pour toute la session : plus aucun
            // survol, tooltip, curseur ou clic ne peut atteindre les panneaux,
            // même ceux qui flottent dans le viewport (toolbar, onglets).
            Ui.PointerInputSuppressed = true;
            Ui.ResetPointerState();
            // Point de référence du drag : confiné au rect (normalement no-op,
            // un appui autorisé est déjà dans le viewport).
            position = ConfineLookCursor(position);
            _lastLookX = position.X;
            _lastLookY = position.Y;
            return;
        }

        if (!_lookAllowed)
            return;

        // Le delta est mesuré sur le mouvement brut de la souris, comme avant :
        // l'orbite reste illimitée même quand le curseur atteint le bord du
        // viewport. Puis la position affichée (dernière + delta) est confinée
        // dans le rect : le curseur OS est ramené dans le viewport s'il en
        // sortirait, sans jamais tronquer le delta d'orbite.
        var deltaX = position.X - _lastLookX;
        var deltaY = position.Y - _lastLookY;
        Camera.Yaw += deltaX * 0.003f;
        Camera.Pitch = Math.Clamp(Camera.Pitch - deltaY * 0.003f, -1.45f, 1.45f);
        position = ConfineLookCursor(new Vector2(_lastLookX + deltaX, _lastLookY + deltaY));
        _lastLookX = position.X;
        _lastLookY = position.Y;
    }

    /// <summary>Rend le curseur OS à l'UI s'il avait été masqué par l'orbite.</summary>
    private void RestoreCursorIfHidden()
    {
        if (!_cursorHidden)
            return;
        Mouse.SetCursorVisible(true);
        _cursorHidden = false;
    }

    /// <summary>
    /// Confine la position affichée du curseur dans
    /// <see cref="MouseLookClampRect"/> : s'il en sortirait, il est ramené au
    /// bord côté OS (warp) et la position renvoyée sert de référence au
    /// prochain delta. Le delta d'orbite, lui, est mesuré sur le mouvement
    /// brut avant ce confinement : pousser contre un bord continue de tourner
    /// pendant que le curseur reste coincé dans le viewport.
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
    /// Rectangle (pixels fenêtre, origine en haut à gauche) dans lequel le
    /// curseur est confiné pour la durée d'une session d'orbite, ou null pour
    /// le laisser libre. L'éditeur le borne au viewport docké : la souris ne
    /// peut pas sortir de la scène pendant un drag, même masquée.
    /// </summary>
    protected virtual UiRect? MouseLookClampRect => null;

    /// <summary>
    /// True when the bound movement keys may move the camera. Subclasses
    /// disable it while the UI is consuming the keyboard (a focused text
    /// input must not fight the camera for the same keystrokes).
    /// </summary>
    protected virtual bool CanMoveCamera() => true;

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
