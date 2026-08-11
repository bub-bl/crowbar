using Crowbar.Engine.InputSystem;

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
