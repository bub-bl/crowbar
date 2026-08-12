namespace Crowbar.UI;

/// <summary>
/// Live values the editor's status bar displays. The application writes them
/// every frame; the Razor editor page reads them and re-renders on a
/// throttle (the page's <c>BuildHash</c> includes a time bucket), so the
/// numbers update a few times per second without rebuilding the UI every
/// frame.
/// </summary>
public static class UiDiagnostics
{
    /// <summary>Current frame rate (frames per second).</summary>
    public static float Fps;

    /// <summary>Managed heap bytes in use (GC.GetTotalMemory).</summary>
    public static long UsedMemoryBytes;

    /// <summary>Total memory available to the GC (physical RAM).</summary>
    public static long TotalMemoryBytes;

    /// <summary>Network latency estimate in milliseconds (demo value today).</summary>
    public static float PingMs;
}
