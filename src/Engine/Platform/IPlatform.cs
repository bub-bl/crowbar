namespace Crowbar.Engine.Platform;

/// <summary>
/// Platform abstraction over native windowing (SDL on desktop). The platform
/// creates windows and owns the single native event queue: in a multi-window
/// host the application calls <see cref="PumpEvents"/> once per frame and the
/// platform dispatches each native event to the window it belongs to, so every
/// window shares one main-thread loop instead of each running its own.
/// </summary>
public interface IPlatform : IDisposable
{
    IWindow CreateWindow(WindowOptions options);

    /// <summary>
    /// Pumps the native event queue once and dispatches each event to the
    /// window it targets (the windows' own input sources and UI runtimes
    /// react). Raised once per frame by the host before input polling.
    /// </summary>
    void PumpEvents();

    /// <summary>
    /// Usable bounds (the work area that excludes the taskbar/dock) of the
    /// display <paramref name="displayIndex"/> in OS screen coordinates
    /// (top-left origin), or false when the display is unknown. Used to
    /// position windows (e.g. the notification panel at the bottom-left of
    /// the primary display).
    /// </summary>
    bool TryGetDisplayBounds(int displayIndex, out int x, out int y, out int width, out int height);

    /// <summary>
    /// Raised when the platform itself requests a quit (the OS-level SDL
    /// Quit event, e.g. a terminal Ctrl+C or a session end) rather than a
    /// single window's close button. The host closes every window.
    /// </summary>
    event Action? QuitRequested;
}
