using System.Diagnostics;
using Crowbar.Engine.Audio;
using Crowbar.Engine.InputSystem;
using Crowbar.Engine.Platform;
using Crowbar.Engine.Rendering;
using AudioFacade = Crowbar.Engine.Audio.Audio;

namespace Crowbar.Engine;

/// <summary>
/// The application host. Owns the platform, the shared world and audio, the
/// primary <see cref="WindowSession"/> (this instance — the editor window)
/// and any extra windows opened through <see cref="OpenWindow"/>, and drives
/// every session from one main-thread loop in <see cref="Run"/>:
/// <b>events</b> (platform pump, dispatched per window) → <b>input</b>
/// (global facades bound to the focused window) → <b>update</b> (per session:
/// chrome + logic + UI animations, then the shared world/audio) → <b>render</b>
/// (per session: scene + Razor UI through each window's swapchain). Closing
/// the primary window (or the last window) quits the application.
/// </summary>
public abstract class Application : WindowSession
{
    private readonly IPlatform _platform;
    private readonly World _world = new();
    private AudioSystem? _audio;
    private Camera? _camera;
    private readonly List<WindowSession> _extraSessions = [];
    private WebGpuSharedDevice? _sharedGpu;
    private WindowSession? _boundInputSession;
    private bool _quitRequested;
    private bool _disposed;
    private long _lastTick;

    protected Application()
    {
        _platform = CreatePlatform();
        var window = _platform.CreateWindow(CreateWindowOptions());
        Attach(window, CreateGraphicsDevice(window));
        BindDefaultActions();
        World = _world;
        Closed += OnPrimaryClosed;
        _platform.QuitRequested += OnPlatformQuit;
        _lastTick = Stopwatch.GetTimestamp();
    }

    /// <summary>Platform factory; SDL on desktop.</summary>
    protected virtual IPlatform CreatePlatform() => new SdlPlatform();

    protected virtual WindowOptions CreateWindowOptions() => new(Title: "Crowbar", Width: 1280, Height: 720);

    protected IPlatform Platform => _platform;

    /// <summary>The audio engine (mixer + DSP), owned like the world and disposed with the application.</summary>
    protected AudioSystem? AudioSystem => _audio;

    /// <summary>
    /// The viewport camera of the primary window. It is a <see cref="Camera"/>
    /// component owned by a world entity, so the editor hierarchy lists it and
    /// its properties are editable from the inspector like any other
    /// component. Created lazily on first access (and eagerly at initialization
    /// so the hierarchy publish includes it).
    /// </summary>
    public override Camera Camera
    {
        get => _camera ??= CreateCameraEntity();
        protected set => _camera = value;
    }

    /// <summary>
    /// Creates the graphics device for a window. Returns null to run headless
    /// (no surface); subclasses may override for test or alternate backends.
    /// All windows share one <see cref="WebGpuSharedDevice"/>: each gets its
    /// own surface/swapchain against it.
    /// </summary>
    protected virtual IGraphicsDevice? CreateGraphicsDevice(IWindow window)
    {
        if (window.NativeHandle == 0)
            return null; // headless (tests, CI): no window surface to present
        _sharedGpu ??= new WebGpuSharedDevice();
        return new WebGpuContext(_sharedGpu, window.NativeHandle,
            Math.Max(1, window.FramebufferWidth > 0 ? window.FramebufferWidth : window.Width),
            Math.Max(1, window.FramebufferHeight > 0 ? window.FramebufferHeight : window.Height));
    }

    /// <summary>
    /// Opens an extra window and its session (own Razor UI runtime, own
    /// surface/swapchain over the shared WebGPU device, own camera). The
    /// session renders the shared world and is initialized immediately, so it
    /// can be opened at any time from the running loop.
    /// </summary>
    protected WindowSession OpenWindow(WindowOptions options)
    {
        var window = _platform.CreateWindow(options);
        var session = CreateExtraSession(window, CreateGraphicsDevice(window));
        session.World = _world;
        _extraSessions.Add(session);
        session.Initialize();
        return session;
    }

    /// <summary>
    /// Creates the session for an extra window. Subclasses return their own
    /// session type (e.g. one that navigates its UI to a specific Razor page).
    /// </summary>
    protected virtual WindowSession CreateExtraSession(IWindow window, IGraphicsDevice? graphics) =>
        new WindowSession(window, graphics);

    /// <summary>
    /// Binds the movement actions to the keys producing Z, Q, S, D (AZERTY) /
    /// W, A, S, D (QWERTY) in the active layout, plus space/E. The layout is
    /// resolved through the primary window's input source.
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

    /// <summary>
    /// Initializes the shared services (audio, viewport camera) before the
    /// subclass content setup runs.
    /// </summary>
    protected override void OnLoaded()
    {
        InitializeAudio();
        // The viewport camera is a world entity (a "Camera" entity with a
        // Camera component), so it is part of the world before subclasses wire
        // their content — the first hierarchy publish already lists it.
        _ = Camera;
        base.OnLoaded();
    }

    /// <summary>Audio is application-level: torn down when the primary window closes.</summary>
    protected override void OnClosing()
    {
        DisposeAudio();
        base.OnClosing();
    }

    /// <summary>
    /// Runs the application: initializes every session, then the main loop
    /// (pump events → poll input → update sessions + world + audio → render
    /// sessions) until the primary window or the last window closes or the
    /// platform requests a quit. Disposes everything on exit.
    /// </summary>
    public void Run()
    {
        Initialize();
        foreach (var extra in _extraSessions.ToArray())
            extra.Initialize();
        _lastTick = Stopwatch.GetTimestamp();

        while (!_quitRequested)
        {
            // Dispatch each native event to its window (per-window UI input).
            // A close/quit raised here tears the sessions down inside the pump:
            // leave the loop immediately instead of running a frame on them.
            _platform.PumpEvents();
            if (_quitRequested)
                break;
            // The static input facades follow the OS focus: the focused
            // window's source is bound (rebound only when focus switches, so
            // pressed/released edges stay intact across frames).
            BindFocusedInput();
            Input.Poll();

            var now = Stopwatch.GetTimestamp();
            var delta = (now - _lastTick) / (double)Stopwatch.Frequency;
            _lastTick = now;
            var clamped = Math.Clamp((float)delta, 0f, 0.1f);

            // Per-window update (chrome sync, logic, UI animations), then the
            // shared world/audio, then per-window render.
            Update(clamped);
            foreach (var extra in _extraSessions.ToArray())
                extra.Update(clamped);
            _world.Update(clamped);
            _audio?.Update(clamped);
            Render(delta);
            foreach (var extra in _extraSessions.ToArray())
                extra.Render(delta);

            PruneClosedSessions();
        }

        Dispose();
    }

    /// <summary>Binds the focused window's input source to the global facades (once per focus switch).</summary>
    private void BindFocusedInput()
    {
        WindowSession? focused = null;
        foreach (var session in AllSessions())
        {
            if (session.InputSource.IsWindowFocused)
            {
                focused = session;
                break;
            }
        }
        focused ??= AllSessions().FirstOrDefault();
        if (focused is null || ReferenceEquals(focused, _boundInputSession))
            return;
        _boundInputSession = focused;
        Input.Bind(focused.InputSource);
    }

    private IEnumerable<WindowSession> AllSessions()
    {
        yield return this;
        foreach (var extra in _extraSessions)
            yield return extra;
    }

    /// <summary>Closing the primary window quits the application (and closes the extra windows).</summary>
    private void OnPrimaryClosed(WindowSession _)
    {
        _quitRequested = true;
        foreach (var extra in _extraSessions.ToArray())
            extra.Window.Close();
    }

    private void OnPlatformQuit()
    {
        _quitRequested = true;
        foreach (var session in AllSessions())
            session.Window.Close();
    }

    private void PruneClosedSessions()
    {
        _extraSessions.RemoveAll(session => session.Window.IsClosing);
        if (Window.IsClosing && _extraSessions.Count == 0)
            _quitRequested = true;
    }

    /// <summary>
    /// Creates and starts the audio engine. Audio is optional: a missing
    /// device (headless CI, no sound card) degrades to a silent engine instead
    /// of failing startup, and <see cref="Audio"/> stays bound so game code can
    /// call <c>Audio.Play</c> unconditionally.
    /// </summary>
    private void InitializeAudio()
    {
        try
        {
            var backend = CreateAudioBackend();
            _audio = new AudioSystem(backend);
            AudioFacade.Bind(_audio);
            _audio.Start();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Audio] Backend unavailable ({exception.Message}): silent engine.");
            _audio = new AudioSystem();
            AudioFacade.Bind(_audio);
        }
    }

    /// <summary>
    /// Creates the audio backend for the application. Returns null to run
    /// silent (no output device); subclasses may override for test or
    /// alternate backends.
    /// </summary>
    protected virtual IAudioBackend? CreateAudioBackend() => new SdlAudioBackend();

    private void DisposeAudio()
    {
        if (_audio is null)
            return;
        AudioFacade.Unbind();
        _audio.Dispose();
        _audio = null;
    }

    /// <summary>
    /// Spawns the viewport camera as a world-only entity (not part of any
    /// level, so it never serializes with level content) and attaches the
    /// <see cref="Camera"/> component to it.
    /// </summary>
    private Camera CreateCameraEntity()
    {
        var entity = _world.SpawnEntity("Camera");
        var camera = entity.AddComponent<Camera>();
        _camera = camera;
        return camera;
    }

    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var extra in _extraSessions.ToArray())
            extra.Dispose();
        _extraSessions.Clear();
        base.Dispose();
        DisposeAudio();
        _world.Dispose();
        _sharedGpu?.Dispose();
        _sharedGpu = null;
        _platform.Dispose();
    }
}
