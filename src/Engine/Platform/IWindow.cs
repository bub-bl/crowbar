using Crowbar.Engine.InputSystem;
using Crowbar.UI;

namespace Crowbar.Engine.Platform;

public interface IWindow : IDisposable
{
    string Title { get; }
    void SetTitle(string title);
    int Width { get; }
    int Height { get; }
    int FramebufferWidth { get; }
    int FramebufferHeight { get; }
    bool IsClosing { get; }
    nint NativeHandle { get; }

    /// <summary>True while the window is maximized.</summary>
    bool IsMaximized { get; }

    /// <summary>The caption button the cursor is hovering (Windows native chrome).</summary>
    WindowChromeButton HoveredChromeButton { get; }

    /// <summary>
    /// Hands the custom title-bar geometry to the platform so its hit test can
    /// reproduce the native caption (drag + window buttons + snap layouts).
    /// </summary>
    void SetChromeLayout(WindowChromeLayout? layout);

    /// <summary>Raw input source: UI pointer/keyboard events and state polling.</summary>
    IInputSource Input { get; }

    event Action? Loaded;
    event Action? Closing;
    event Action<double>? Updating;
    event Action<double>? Rendering;
    event Action<int, int>? Resized;

    void Run();
    void Close();
}
