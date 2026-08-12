namespace Crowbar.UI;

/// <summary>A single editor toast.</summary>
public sealed record UiNotification(string Title, string Message, string Kind, long TimestampMs);

/// <summary>
/// Editor toast notifications (the analog of a notification center). The
/// application pushes entries — e.g. hot reload results — and the Notifications
/// Razor component renders the most recent ones. The application prunes
/// expired entries each frame; <see cref="Version"/> bumps on every change so
/// components re-render.
/// </summary>
public static class UiNotifications
{
    private const int MaxShown = 4;
    private static readonly List<UiNotification> Items = [];

    /// <summary>Bumped whenever the notification list changes.</summary>
    public static int Version { get; private set; }

    /// <summary>The currently shown notifications, newest last.</summary>
    public static IReadOnlyList<UiNotification> Notifications
    {
        get
        {
            lock (Items)
                return Items.ToArray();
        }
    }

    /// <param name="kind">One of "info", "success" or "error" (drives the toast color).</param>
    public static void Show(string title, string message, string kind = "info")
    {
        lock (Items)
        {
            Items.Add(new UiNotification(title, message, kind, Environment.TickCount64));
            while (Items.Count > MaxShown)
                Items.RemoveAt(0);
            Version++;
        }
    }

    /// <summary>Removes notifications older than <paramref name="maxAgeMs"/> (call once per frame).</summary>
    public static void PruneExpired(long maxAgeMs = 6000)
    {
        lock (Items)
        {
            var cutoff = Environment.TickCount64 - maxAgeMs;
            if (Items.RemoveAll(n => n.TimestampMs < cutoff) > 0)
                Version++;
        }
    }
}
