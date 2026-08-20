namespace Crowbar.Engine.Platform;

public readonly record struct WindowOptions(
    string Title,
    int Width,
    int Height,
    bool Resizable = true,
    /// <summary>True for utility windows (e.g. the notification popup): no taskbar entry while visible.</summary>
    bool SkipTaskbar = false,
    /// <summary>True for popups: no OS decorations (no title bar, no border).</summary>
    bool Borderless = false,
    /// <summary>False to create the window hidden (it shows later via <c>IWindow.SetVisible</c>).</summary>
    bool Visible = true
);

// Note: the notification popup is created with Borderless=true and Visible=false:
// it is not a real window — no decorations, never shown at startup — it is a way
// to display the single latest compilation notification (see NotificationWindow).
