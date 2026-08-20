using Crowbar.Editor;

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

    /// <summary>Application working-set bytes in use, including native allocations.</summary>
    public static long UsedMemoryBytes;

    /// <summary>Total memory available to the GC (physical RAM).</summary>
    public static long TotalMemoryBytes;

    /// <summary>Network latency estimate in milliseconds (demo value today).</summary>
    public static float PingMs;

    /// <summary>
    /// Live status bar entries published by game code (see
    /// <see cref="StatusBar"/>), or empty when none. The application re-snapshots
    /// them every frame; the editor page re-renders on a throttle.
    /// </summary>
    public static IReadOnlyList<StatusBarEntry> StatusBarEntries = [];

    /// <summary>Formats a byte count without rounding small non-zero values down to 0 GB.</summary>
    public static string FormatMemory(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1_024) return $"{bytes:0} B";
        if (bytes < 1_048_576) return $"{bytes / 1_024.0:0.0} KB";
        if (bytes < 1_073_741_824) return $"{bytes / 1_048_576.0:0.0} MB";
        return $"{bytes / 1_073_741_824.0:0.00} GB";
    }
}
