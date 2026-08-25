using Silk.NET.Maths;
using Silk.NET.SDL;
using Crowbar.UI;

namespace Crowbar.Engine.Platform;

/// <summary>
/// SDL2 platform: initializes SDL once, creates <see cref="SdlWindow"/>
/// windows backed by real SDL windows, and pumps the single SDL event queue
/// in <see cref="PumpEvents"/>, dispatching each event to the window it
/// belongs to (by SDL window ID). All input (keyboard, mouse, text, wheel)
/// flows through SDL, so the platform is fully cross-platform.
/// </summary>
public sealed unsafe class SdlPlatform : IPlatform
{
    private readonly Sdl _sdl;
    private readonly Dictionary<uint, SdlWindow> _windows = [];
    private bool _disposed;

    public SdlPlatform()
    {
        _sdl = Sdl.GetApi();
        _sdl.SetMainReady();
        if (_sdl.Init(Sdl.InitVideo) < 0)
            throw new InvalidOperationException($"SDL initialization failed: {_sdl.GetErrorS()}");

        // Bridge the UI clipboard to SDL's system clipboard so Ctrl+C / Ctrl+V
        // in editor inputs use the real clipboard instead of an in-process one.
        Clipboard.SetPlatformBridge(
            read: () =>
            {
                var ptr = _sdl.GetClipboardText();
                try { return ptr == null ? string.Empty : SdlPtrToUtf8(ptr); }
                finally { _sdl.Free(ptr); }
            },
            write: text =>
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty);
                Array.Resize(ref bytes, bytes.Length + 1); // NUL terminator for SDL
                fixed (byte* p = bytes) _sdl.SetClipboardText(p);
            });
    }

    // Reads a NUL-terminated UTF-8 byte* into a managed string (flat, no copy).
    private static unsafe string SdlPtrToUtf8(byte* p)
    {
        var length = 0;
        while (p[length] != 0) length++;
        return System.Text.Encoding.UTF8.GetString(p, length);
    }

    public IWindow CreateWindow(WindowOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var window = new SdlWindow(this, _sdl, options);
        _windows[window.WindowId] = window;
        return window;
    }

    public void PumpEvents()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var e = new Event();
        while (_sdl.PollEvent(ref e) != 0)
        {
            var windowId = WindowIdOf(e);
            if (_windows.TryGetValue(windowId, out var window))
            {
                window.HandleEvent(in e);
            }
            else if ((EventType)e.Type == EventType.Quit)
            {
                // SDL's quit event carries no window id: the OS asked the
                // whole application to quit. Close every window through the
                // host (which subscribes to QuitRequested).
                QuitRequested?.Invoke();
            }
        }
    }

    public bool TryGetDisplayBounds(int displayIndex, out int x, out int y, out int width, out int height)
    {
        // Use the display's usable bounds (the work area that excludes the OS
        // taskbar/dock), so windows positioned against the bottom edge never
        // end up underneath the taskbar.
        var rect = new Rectangle<int>();
        if (_sdl.GetDisplayUsableBounds(displayIndex, ref rect) != 0)
        {
            x = y = width = height = 0;
            return false;
        }

        x = rect.Origin.X;
        y = rect.Origin.Y;
        width = rect.Size.X;
        height = rect.Size.Y;
        return true;
    }

    public event Action? QuitRequested;

    /// <summary>
    /// The SDL window id an event targets. Each SDL event struct carries its
    /// own windowID field (window events, motion, buttons, wheel, key, text);
    /// the global quit event has none and reads as 0.
    /// </summary>
    private static uint WindowIdOf(in Event e) => ((EventType)e.Type) switch
    {
        EventType.Windowevent => e.Window.WindowID,
        EventType.Mousemotion => e.Motion.WindowID,
        EventType.Mousebuttondown or EventType.Mousebuttonup => e.Button.WindowID,
        EventType.Mousewheel => e.Wheel.WindowID,
        EventType.Keydown or EventType.Keyup => e.Key.WindowID,
        EventType.Textinput => e.Text.WindowID,
        _ => 0
    };

    internal void UnregisterWindow(SdlWindow window) => _windows.Remove(window.WindowId);

    public void Dispose()
    {
        if (_disposed)
            return;

        _sdl.Quit();
        _sdl.Dispose();
        _windows.Clear();
        _disposed = true;
    }
}
