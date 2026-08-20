namespace Crowbar.UI;

/// <summary>
/// Request channel (UI → host) for toggling the notification window: the
/// toolbar bell and the Ctrl+Shift+N shortcut call <see cref="RequestToggle"/>,
/// and the host consumes it each frame to show or completely hide the
/// persistent notification window (created once, never destroyed). The host
/// owns the window; this static only mirrors the pending request, exactly like
/// <see cref="EditorProjectState"/>.
/// </summary>
public static class EditorNotificationWindowState
{
    private static readonly Lock RequestLock = new();
    private static bool _toggleRequested;

    /// <summary>Queues a show/hide toggle request for the host to consume this frame.</summary>
    public static void RequestToggle()
    {
        lock (RequestLock)
            _toggleRequested = true;
    }

    /// <summary>Returns and clears the pending toggle request, or false.</summary>
    public static bool ConsumeToggleRequest()
    {
        lock (RequestLock)
        {
            var requested = _toggleRequested;
            _toggleRequested = false;
            return requested;
        }
    }
}
