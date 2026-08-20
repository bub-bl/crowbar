using System.Diagnostics;
using Crowbar.Engine.InputSystem;
using Crowbar.UI;
using Silk.NET.SDL;

namespace Crowbar.Engine.Platform;

/// <summary>
/// SDL2-backed <see cref="IWindow"/>. Owns the native window and raises the
/// UI input events; the owning <see cref="SdlPlatform"/> pumps the shared SDL
/// queue and forwards each event to the window it targets through
/// <see cref="HandleEvent"/>. The native handle is the Windows HWND when
/// available, which the WebGPU surface needs. The window never binds the
/// global <see cref="Input"/> facade itself: the multi-window host binds the
/// focused window's input source instead.
/// </summary>
internal sealed unsafe class SdlWindow : IWindow
{
    private const int WindowPosCentered = 0x2FFF0000;
    private const uint WindowShown = 4;
    private const uint WindowHidden = 8;
    private const uint WindowBorderless = 16;
    private const uint WindowResizable = 32;
    private const uint WindowAllowHighDpi = 8192;

    private readonly SdlPlatform _platform;
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
    private KeyEvent? _pendingKeyDown;

    public SdlWindow(SdlPlatform platform, Sdl sdl, WindowOptions options)
    {
        _platform = platform;
        _sdl = sdl;
        _title = options.Title;
        _width = options.Width;
        _height = options.Height;
        _drawableWidth = options.Width;
        _drawableHeight = options.Height;

        var flags = (options.Visible ? WindowShown : WindowHidden)
                  | (options.Resizable ? WindowResizable : 0)
                  | WindowAllowHighDpi
                  | (options.SkipTaskbar ? (uint)WindowFlags.SkipTaskbar : 0)
                  | (options.Borderless ? WindowBorderless : 0);
        _window = sdl.CreateWindow(options.Title, WindowPosCentered, WindowPosCentered, options.Width, options.Height, flags);
        if (_window == null)
            throw new InvalidOperationException($"SDL window creation failed: {sdl.GetErrorS()}");

        RefreshSizes();
        // Global text input lets SDL translate key presses into composed UTF-8
        // (accents, IME), which the UI's TextInput widgets consume. SDL routes
        // text input to the focused window, so calling it once per window is
        // harmless (the focused window receives the composed text).
        sdl.StartTextInput();
        _input = new SdlInputSource(sdl, _window);
        // Borderless popups have no caption to replace: no custom chrome.
        if (OperatingSystem.IsWindows() && !options.Borderless)
        {
            // Replace the native caption with the custom title bar (drag,
            // minimize/maximize/close and the Windows 11 snap layouts are all
            // answered by the OS through WM_NCHITTEST).
            _chrome = new Win32WindowChrome(NativeHandle, _drawableWidth, _drawableHeight);
            // The caption is now part of the client area: re-read the sizes.
            RefreshSizes();
        }
        _input.SetViewportScale(ScaleX, ScaleY);
    }

    /// <summary>The SDL window id used by <see cref="SdlPlatform.PumpEvents"/> to route events to this window.</summary>
    internal uint WindowId => _sdl.GetWindowID(_window);

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

    public bool IsVisible => (_sdl.GetWindowFlags(_window) & (uint)WindowFlags.Shown) != 0;

    public void SetPosition(int x, int y) => _sdl.SetWindowPosition(_window, x, y);

    public void SetVisible(bool visible)
    {
        if (visible)
            _sdl.ShowWindow(_window);
        else
            _sdl.HideWindow(_window);
    }

    public void SetInputFocus() => _sdl.SetWindowInputFocus(_window);

    public WindowChromeState ChromeState => new(
        _chrome?.HoveredButton ?? WindowChromeButton.None,
        IsMaximized,
        _focused,
        _fullscreen,
        _chrome is not null);

    public void SetChromeLayout(WindowChromeLayout? layout) =>
        _chrome?.SetLayout(layout);

    /// <summary>
    /// Enters or leaves borderless desktop fullscreen (SDL_WINDOW_FULLSCREEN_DESKTOP).
    /// Unlike exclusive display-mode fullscreen, this keeps Windows overlays such
    /// as Win+Shift+S available without making the application leave fullscreen.
    /// The caption/client geometry changes with the mode, so the sizes are
    /// re-read immediately.
    /// </summary>
    public void SetFullscreen(bool fullscreen)
    {
        if (_fullscreen == fullscreen)
            return;
        _fullscreen = fullscreen;
        // Tell the Win32 chrome first: the fullscreen switch triggers a
        // WM_NCCALCSIZE/WM_NCHITTEST burst that must already see the new mode.
        _chrome?.SetFullscreen(fullscreen);
        _sdl.SetWindowFullscreen(_window, fullscreen ? (uint)WindowFlags.FullscreenDesktop : 0);
        RefreshSizes();
        // SDL's window grab confines the physical cursor to this window. Apply
        // it after switching modes so the grab uses the fullscreen bounds.
        _input.SetMouseGrabbed(fullscreen && _focused);
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

    public event Action? Closing;
    public event Action<int, int>? Resized;

    public void Close() => RequestClose();

    /// <summary>
    /// Handles one SDL event addressed to this window (dispatched by
    /// <see cref="SdlPlatform.PumpEvents"/>), raising the UI input events the
    /// owning session wired to its UI runtime.
    /// </summary>
    internal void HandleEvent(in Event e)
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

        // Restore/FocusGained can arrive before Windows has committed the final
        // client and drawable dimensions. Refresh after the events of this
        // window have been drained so any queued SizeChanged event has already
        // been observed.
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
                // Maximized is commonly delivered immediately after Restored.
                // During that handoff SDL can still report a 0/1 px drawable.
                // Do not publish those transient metrics: they would resize the
                // renderer and change the chrome hit-test scale, leaving the
                // titlebar buttons inert until another native mouse/resize event.
                _restoreMetricsPending = !RefreshWindowMetrics(requireUsableSize: true);
                break;

            case WindowEventID.Minimized:
                // A minimized window has no usable swapchain surface. Keep the
                // state explicit so the render loop does not repeatedly acquire
                // from a zero-sized surface and starve the UI's next restore.
                _minimized = true;
                _restoreMetricsPending = false;
                _focused = false;
                _input.SetFocused(false);
                _input.SetMouseGrabbed(false);
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
                _input.SetMouseGrabbed(_fullscreen);
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
                _input.SetMouseGrabbed(_fullscreen);
                break;

            case WindowEventID.FocusLost:
                _focused = false;
                _input.SetFocused(false);
                _input.SetMouseGrabbed(false);
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
        _input.SetMouseGrabbed(false);
        _chrome?.Dispose();
        _chrome = null;
        _platform.UnregisterWindow(this);
        if (_window != null)
            _sdl.DestroyWindow(_window);
    }
}
