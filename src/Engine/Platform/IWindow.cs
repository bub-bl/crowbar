using Crowbar.Engine.InputSystem;
using Crowbar.UI;

namespace Crowbar.Engine.Platform;

/// <summary>
/// A native window. The owning <see cref="IPlatform"/> pumps the shared event
/// queue and dispatches each event to its window; a <see cref="WindowSession"/>
/// wires the window's input into a UI runtime and drives its frames from the
/// host loop. The window itself never runs a blocking loop.
/// </summary>
public interface IWindow : IDisposable
{
    string Title { get; }
    void SetTitle(string title);
    int Width { get; }
    int Height { get; }
    int FramebufferWidth { get; }
    int FramebufferHeight { get; }
    bool IsClosing { get; }
    /// <summary>True while the OS has removed the window from the usable swapchain surface.</summary>
    bool IsMinimized { get; }

    /// <summary>True while the window is shown on screen (not hidden with <see cref="SetVisible"/>).</summary>
    bool IsVisible { get; }

    nint NativeHandle { get; }

    /// <summary>Moves the window to a position in OS screen coordinates (top-left origin).</summary>
    void SetPosition(int x, int y);

    /// <summary>Shows or completely hides the window (hidden windows keep their session alive).</summary>
    void SetVisible(bool visible);

    /// <summary>
    /// Gives the window the OS keyboard focus. The host restores focus to the
    /// primary window after showing a popup (e.g. the notification window)
    /// so the popup never steals the editor's keystrokes.
    /// </summary>
    void SetInputFocus();

    /// <summary>
    /// Live window-chrome state: the hovered caption button, maximized/active/
    /// fullscreen flags and whether this platform draws a custom client-area
    /// caption (Windows). The host mirrors it into the UI every frame.
    /// </summary>
    WindowChromeState ChromeState { get; }

    /// <summary>
    /// Toggles borderless desktop fullscreen (the whole display is covered
    /// without taking exclusive control of the display mode) or restores the
    /// previous windowed state.
    /// </summary>
    void SetFullscreen(bool fullscreen);

    /// <summary>
    /// Hands the custom title-bar geometry to the platform so its hit test can
    /// reproduce the native caption (drag + window buttons + snap layouts).
    /// </summary>
    void SetChromeLayout(WindowChromeLayout? layout);

    /// <summary>Raw input source: UI pointer/keyboard events and state polling.</summary>
    IInputSource Input { get; }

    event Action? Closing;
    event Action<int, int>? Resized;

    void Close();
}
