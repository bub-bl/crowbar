using System.Diagnostics;
using Crowbar.Engine.InputSystem;
using Crowbar.UI;
using Silk.NET.SDL;

namespace Crowbar.Engine.Platform;

/// <summary>
/// SDL2-backed <see cref="IWindow"/>. Owns the native window and its event
/// loop; pumps SDL events (quit, resize, focus, mouse, keyboard, text) into the
/// UI input contract and exposes a polling <see cref="IInputSource"/> for the
/// runtime input facades. The native handle is the Windows HWND when available,
/// which the WebGPU surface needs.
/// </summary>
internal sealed unsafe class SdlWindow : IWindow
{
    private const int WindowPosCentered = 0x2FFF0000;
    private const uint WindowShown = 4;
    private const uint WindowResizable = 32;
    private const uint WindowAllowHighDpi = 8192;

    private readonly Sdl _sdl;
    private readonly Window* _window;
    private readonly SdlInputSource _input;
    private Win32WindowChrome? _chrome;
    private string _title;
    private int _width;
    private int _height;
    private int _drawableWidth;
    private int _drawableHeight;
    private bool _focused = true;
    private bool _fullscreen;
    private bool _minimized;
    private bool _restoreMetricsPending;
    private bool _closing;
    private bool _disposed;
    private long _lastTick;
    private KeyEvent? _pendingKeyDown;

    public SdlWindow(Sdl sdl, WindowOptions options)
    {
        _sdl = sdl;
        _title = options.Title;
        _width = options.Width;
        _height = options.Height;
        _drawableWidth = options.Width;
        _drawableHeight = options.Height;

        var flags = WindowShown | (options.Resizable ? WindowResizable : 0) | WindowAllowHighDpi;
        _window = sdl.CreateWindow(options.Title, WindowPosCentered, WindowPosCentered, options.Width, options.Height, flags);
        if (_window == null)
            throw new InvalidOperationException($"SDL window creation failed: {sdl.GetErrorS()}");

        RefreshSizes();
        // Global text input lets SDL translate key presses into composed UTF-8
        // (accents, IME), which the UI's TextInput widgets consume.
        sdl.StartTextInput();
        _input = new SdlInputSource(sdl, _window);
        if (OperatingSystem.IsWindows())
        {
            // Replace the native caption with the custom title bar (drag,
            // minimize/maximize/close and the Windows 11 snap layouts are all
            // answered by the OS through WM_NCHITTEST).
            _chrome = new Win32WindowChrome(NativeHandle, _drawableWidth, _drawableHeight);
            // The caption is now part of the client area: re-read the sizes.
            RefreshSizes();
        }
        _input.SetViewportScale(ScaleX, ScaleY);
        Crowbar.Engine.InputSystem.Input.Bind(_input);
        _lastTick = Stopwatch.GetTimestamp();
    }

    private float ScaleX => _drawableWidth > 0 ? _drawableWidth / (float)Math.Max(1, _width) : 1f;
    private float ScaleY => _drawableHeight > 0 ? _drawableHeight / (float)Math.Max(1, _height) : 1f;

    public IInputSource Input => _input;

    public string Title => _title;

    public void SetTitle(string title)
    {
        _title = title;
        _sdl.SetWindowTitle(_window, title);
    }

    public int Width => _width;
    public int Height => _height;
    public int FramebufferWidth => _drawableWidth;
    public int FramebufferHeight => _drawableHeight;
    public bool IsClosing => _closing;
    public bool IsMinimized => _minimized;

    public WindowChromeState ChromeState => new(
        _chrome?.HoveredButton ?? WindowChromeButton.None,
        IsMaximized,
        _focused,
        _fullscreen,
        _chrome is not null);

    public void SetChromeLayout(WindowChromeLayout? layout) =>
        _chrome?.SetLayout(layout);

    /// <summary>
    /// Enters or leaves exclusive fullscreen (SDL_WINDOW_FULLSCREEN), the same
    /// mode standalone Unreal/Unity games use: the window takes over the whole
    /// display, with no borders or taskbar. The caption/client geometry changes
    /// with the mode, so the sizes are re-read immediately.
    /// </summary>
    public void SetFullscreen(bool fullscreen)
    {
        if (_fullscreen == fullscreen)
            return;
        _fullscreen = fullscreen;
        // Tell the Win32 chrome first: the fullscreen switch triggers a
        // WM_NCCALCSIZE/WM_NCHITTEST burst that must already see the new mode.
        _chrome?.SetFullscreen(fullscreen);
        _sdl.SetWindowFullscreen(_window, fullscreen ? (uint)WindowFlags.Fullscreen : 0);
        RefreshSizes();
    }

    private bool IsMaximized =>
        (_sdl.GetWindowFlags(_window) & (uint)WindowFlags.Maximized) != 0;

    /// <summary>
    /// The Windows HWND backing this SDL window, or 0 on other platforms. The
    /// WebGPU surface is created from it, so the window must exist.
    /// </summary>
    public nint NativeHandle
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return 0;

            var info = new SysWMInfo
            {
                Version = new Silk.NET.SDL.Version { Major = 2, Minor = 0, Patch = 0 }
            };
            if (_sdl.GetWindowWMInfo(_window, &info) && info.Subsystem == SysWMType.Windows)
            {
                return info.Info.Win.Hwnd;
            }

            return 0;
        }
    }

    public event Action? Loaded;
    public event Action? Closing;
    public event Action<double>? Updating;
    public event Action<double>? Rendering;
    public event Action<int, int>? Resized;

    public void Run()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Loaded?.Invoke();
        while (!_closing && !_disposed)
        {
            PollEvents();
            var now = Stopwatch.GetTimestamp();
            var delta = (now - _lastTick) / (double)Stopwatch.Frequency;
            _lastTick = now;
            Updating?.Invoke(delta);
            Rendering?.Invoke(delta);
        }
    }

    public void Close() => RequestClose();

    private void PollEvents()
    {
        var e = new Event();
        while (!_closing && _sdl.PollEvent(ref e) != 0)
        {
            switch ((EventType)e.Type)
            {
                case EventType.Quit:
                    RequestClose();
                    break;

                case EventType.Windowevent:
                    HandleWindowEvent(e.Window);
                    break;

                case EventType.Mousemotion:
                    _input.RaisePointerMoved(new PointerMoveEvent(e.Motion.X * ScaleX, e.Motion.Y * ScaleY));
                    break;

                case EventType.Mousebuttondown:
                case EventType.Mousebuttonup:
                    HandleMouseButton(e.Button, isDown: (EventType)e.Type == EventType.Mousebuttondown);
                    break;

                case EventType.Mousewheel:
                    HandleWheel(e.Wheel);
                    break;

                case EventType.Keydown:
                    // The next TextInput event (if any) carries the composed text
                    // for this keypress; the event fires once, with the text attached.
                    // The VK code comes from the SDL keycode (logical, layout-aware),
                    // not the scancode: the UI's Ctrl shortcuts and text handling
                    // work on the character a key produces, so Ctrl+A is VK_A on
                    // every layout.
                    FlushPendingKey();
                    _pendingKeyDown = new KeyEvent(
                        ToWindowsVk(e.Key.Keysym),
                        IsDown: true,
                        IsRepeat: e.Key.Repeat != 0,
                        Text: null);
                    break;

                case EventType.Textinput:
                    HandleTextInput(e.Text);
                    break;

                case EventType.Keyup:
                    FlushPendingKey();
                    _input.RaiseKeyChanged(new KeyEvent(
                        ToWindowsVk(e.Key.Keysym),
                        IsDown: false,
                        IsRepeat: false,
                        Text: null));
                    break;

                default:
                    FlushPendingKey();
                    break;
            }
        }

        // Restore/FocusGained can arrive before Windows has committed the final
        // client and drawable dimensions. Refresh after the SDL queue has been
        // drained so any queued SizeChanged event has already been observed.
        if (_restoreMetricsPending && !_minimized && RefreshWindowMetrics(requireUsableSize: true))
            _restoreMetricsPending = false;
    }

    private void HandleWindowEvent(WindowEvent window)
    {
        switch ((WindowEventID)window.Event)
        {
            case WindowEventID.Close:
                RequestClose();
                break;

            case WindowEventID.Resized:
            case WindowEventID.SizeChanged:
            case WindowEventID.Maximized:
                _minimized = false;
                _restoreMetricsPending = false;
                RefreshWindowMetrics();
                break;

            case WindowEventID.Minimized:
                // A minimized window has no usable swapchain surface. Keep the
                // state explicit so the render loop does not repeatedly acquire
                // from a zero-sized surface and starve the UI's next restore.
                _minimized = true;
                _restoreMetricsPending = false;
                _focused = false;
                _input.SetFocused(false);
                FlushPendingKey();
                break;

            case WindowEventID.Restored:
                // SDL does not guarantee a Resized event after a taskbar restore.
                // Refresh the drawable size and force the renderer/UI viewport to
                // rebuild before the next frame is submitted.
                _minimized = false;
                _restoreMetricsPending = true;
                _focused = true;
                _input.SetFocused(true);
                break;

            case WindowEventID.FocusGained:
                // Some Windows/SDL combinations report the taskbar restore as
                // focus gained without a separate RESTORED event. Treat focus
                // gain as a usable window and refresh the swapchain if this was
                // the first usable event after minimization.
                var wasMinimized = _minimized;
                _minimized = false;
                if (wasMinimized)
                    _restoreMetricsPending = true;
                _focused = true;
                _input.SetFocused(true);
                break;

            case WindowEventID.FocusLost:
                _focused = false;
                _input.SetFocused(false);
                FlushPendingKey();
                break;
        }
    }

    private void HandleMouseButton(MouseButtonEvent button, bool isDown)
    {
        var x = button.X * ScaleX;
        var y = button.Y * ScaleY;
        _input.RaisePointerButtonChanged(new PointerButtonEvent(x, y, SdlButtonToPointer(button.Button), isDown));
    }

    private void HandleWheel(MouseWheelEvent wheel)
    {
        // SDL2 delivers ±1 "notches" (or precise fractional deltas on
        // trackpads); a flipped direction means the deltas are inverted
        // relative to the OS convention, so they are negated once. The UI
        // applies its own scroll step to these units.
        var flipped = wheel.Direction == (uint)MouseWheelDirection.Flipped;
        var deltaX = wheel.PreciseX != 0f ? wheel.PreciseX : wheel.X;
        var deltaY = wheel.PreciseY != 0f ? wheel.PreciseY : wheel.Y;
        if (flipped)
        {
            deltaX = -deltaX;
            deltaY = -deltaY;
        }

        _input.AccumulateWheel(new System.Numerics.Vector2(deltaX, deltaY));
        _input.RaisePointerWheelChanged(new PointerWheelEvent(
            wheel.MouseX * ScaleX, wheel.MouseY * ScaleY, deltaX, deltaY));
    }

    private void HandleTextInput(TextInputEvent text)
    {
        var typed = ReadText(text);
        if (_pendingKeyDown is { } pending)
        {
            _pendingKeyDown = null;
            // Ctrl/Alt chords are shortcuts, not text: keep the key event (so
            // Ctrl+A selects all, Ctrl+C copies, ...) but drop the inserted text.
            var mods = _sdl.GetModState();
            var chord = (mods & Keymod.Ctrl) != 0 || (mods & Keymod.Alt) != 0;
            _input.RaiseKeyChanged(pending with { Text = chord ? null : typed });
        }
        else
        {
            // Text with no preceding KeyDown (IME composition result).
            _input.RaiseKeyChanged(new KeyEvent(0, IsDown: true, IsRepeat: false, Text: typed));
        }
    }

    /// <summary>SDL keycode (layout-aware) → Windows VK for the UI key contract.</summary>
    private int ToWindowsVk(Keysym sym) =>
        KeyMapping.ToWindowsVk(_sdl.GetKeyFromScancode(sym.Scancode));

    /// Fires a pending KeyDown that never got a TextInput companion (navigation
    /// keys, modifiers, Enter, ...) with no text, so the UI still sees it.
    /// </summary>
    private void FlushPendingKey()
    {
        if (_pendingKeyDown is not { } pending)
            return;
        _pendingKeyDown = null;
        _input.RaiseKeyChanged(pending);
    }

    /// <summary>Reads SDL's fixed 32-byte UTF-8 text buffer into a managed string.</summary>
    private static string? ReadText(TextInputEvent text)
    {
        // Accessing a fixed buffer on a local yields a byte* into that local's
        // storage, which stays valid for the duration of this method.
        var p = text.Text;
        var length = 0;
        while (length < 32 && p[length] != 0)
            length++;
        return length == 0 ? null : System.Text.Encoding.UTF8.GetString(p, length);
    }

    private static PointerButton SdlButtonToPointer(byte button) => button switch
    {
        1 => PointerButton.Left,
        2 => PointerButton.Middle,
        3 => PointerButton.Right,
        4 => PointerButton.X1,
        5 => PointerButton.X2,
        _ => PointerButton.Left
    };

    private bool RefreshWindowMetrics(bool requireUsableSize = false)
    {
        var width = 0;
        var height = 0;
        var drawableWidth = 0;
        var drawableHeight = 0;
        _sdl.GetWindowSize(_window, ref width, ref height);
        _sdl.VulkanGetDrawableSize(_window, ref drawableWidth, ref drawableHeight);
        if (requireUsableSize && (width <= 1 || height <= 1 || drawableWidth <= 1 || drawableHeight <= 1))
            return false;

        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _drawableWidth = Math.Max(1, drawableWidth);
        _drawableHeight = Math.Max(1, drawableHeight);
        _chrome?.SetDrawableSize(_drawableWidth, _drawableHeight);
        _input.SetViewportScale(ScaleX, ScaleY);
        Resized?.Invoke(_drawableWidth, _drawableHeight);
        return true;
    }

    private void RefreshSizes()
    {
        _sdl.GetWindowSize(_window, ref _width, ref _height);
        _sdl.VulkanGetDrawableSize(_window, ref _drawableWidth, ref _drawableHeight);
        _width = Math.Max(1, _width);
        _height = Math.Max(1, _height);
        _drawableWidth = Math.Max(1, _drawableWidth);
        _drawableHeight = Math.Max(1, _drawableHeight);
        _chrome?.SetDrawableSize(_drawableWidth, _drawableHeight);
    }

    private void RequestClose()
    {
        if (_closing)
            return;
        _closing = true;
        Closing?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _closing = true;
        _chrome?.Dispose();
        _chrome = null;
        if (_window != null)
            _sdl.DestroyWindow(_window);
    }
}
