namespace Crowbar.UI;

/// <summary>A single editor notification.</summary>
public sealed record UiNotification(string Title, string Message, string Kind, long TimestampMs)
{
    /// <summary>Wall-clock time the notification was raised (kept for diagnostics).</summary>
    public DateTime Time { get; init; } = DateTime.Now;
}

/// <summary>
/// Editor notifications. The application pushes entries — e.g. hot reload /
/// compilation results — and two consumers render them: the transient toasts
/// (<see cref="Notifications"/>, pruned after a few seconds, shown in the
/// editor page) and the notification popup (<see cref="Latest"/>, a single
/// notification — the last one pushed — shown in the bottom-left borderless
/// window). The popup never shows a feed: each new notification replaces the
/// previous one. <see cref="Version"/> bumps on every change so components
/// re-render.
/// </summary>
public static class UiNotifications
{
    private const int MaxShown = 4;
    private static readonly List<UiNotification> Items = [];
    private static UiNotification? _latest;

    /// <summary>Bumped whenever the toasts or the latest notification change.</summary>
    public static int Version { get; private set; }

    /// <summary>The currently shown toasts, newest last (pruned after a few seconds).</summary>
    public static IReadOnlyList<UiNotification> Notifications
    {
        get
        {
            lock (Items)
                return Items.ToArray();
        }
    }

    /// <summary>
    /// The single notification displayed by the notification window: the last
    /// one pushed, replaced on each new notification. The window never shows
    /// several notifications at once.
    /// </summary>
    public static UiNotification? Latest
    {
        get
        {
            lock (Items)
                return _latest;
        }
    }

    /// <param name="kind">One of "info", "success" or "error" (drives the popup color).</param>
    public static void Show(string title, string message, string kind = "info")
    {
        lock (Items)
        {
            var notification = new UiNotification(title, message, kind, Environment.TickCount64);
            Items.Add(notification);
            while (Items.Count > MaxShown)
                Items.RemoveAt(0);
            _latest = notification;
            Version++;
        }
    }

    /// <summary>Removes toasts older than <paramref name="maxAgeMs"/> (call once per frame). The latest notification is never pruned.</summary>
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
