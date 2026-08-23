namespace Crowbar.Engine.Global;

/// <summary>Severity of a log message.</summary>
public enum LogLevel
{
    /// <summary>Normal progress messages (project, level, script lifecycle).</summary>
    Info,
    /// <summary>Recoverable problems that fell back to a working state (unreadable file, failed reload, ...).</summary>
    Warning,
    /// <summary>Failures that break a feature (reserved for the severe paths).</summary>
    Error
}

/// <summary>
/// A single log entry captured by the <see cref="Log"/> API.
/// <see cref="Details"/> holds sub-lines displayed below the message in the
/// console (e.g. a command list), like an error's stack trace but for any
/// level. It is settable so the console UI can attach a command's output to
/// the echoed command line after execution.
/// </summary>
public sealed record LogEntry(string Message, LogLevel Level, DateTime Timestamp, string? StackTrace = null,
    Exception? Exception = null)
{
    /// <summary>Sub-lines displayed below the message when the entry is expanded.</summary>
    public IReadOnlyList<string>? Details { get; set; }
}

/// <summary>
/// The logging API — the s&amp;box-style <c>Log</c>, equivalent of the console.
/// Exposed globally as <see cref="GlobalNamespaces.Log"/>; editor and game code
/// report progress and failures through <see cref="Info"/>/<see cref="Warn"/>/
/// <see cref="Error"/> instead of writing to the console directly, so the
/// console UI can inspect, filter and display the output without touching call
/// sites. The messages carry their own domain tags (<c>[Project]</c>,
/// <c>[Level]</c>, <c>[Scripting]</c>, ...).
/// The single instance exposed through <see cref="GlobalNamespaces.Log"/> is the
/// application-wide logger. Shared state (entries, event) lives on the instance
/// so Razor components can access it through the <c>Log</c> property imported
/// via <c>@using static</c> without C#'s instance-vs-static restrictions.
/// </summary>
public sealed class Log
{
    private const int MaxBufferedEntries = 1000;
    private readonly object _lock = new();

    /// <summary>
    /// Fired on the calling thread whenever a message is logged. Subscribers
    /// (the console UI) must enqueue for the UI thread if needed.
    /// </summary>
    public event Action<LogEntry>? EntryAdded;

    /// <summary>
    /// All log entries captured since startup, newest last. Capped at
    /// <see cref="MaxBufferedEntries"/> (oldest pruned). The console UI
    /// reads this on first show to catch up on early entries.
    /// </summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock)
                return _entries.ToArray();
        }
    }

    private readonly List<LogEntry> _entries = new(MaxBufferedEntries + 16);

    /// <summary>Normal progress messages (project, level, script lifecycle). Returns the created entry.</summary>
    public LogEntry Info(string message) => Write(message, LogLevel.Info);

    /// <summary>
    /// Normal progress message with sub-lines (e.g. a list) shown below it in
    /// the console, expandable like an error's stack trace. Returns the
    /// created entry.
    /// </summary>
    public LogEntry Info(string message, IReadOnlyList<string> details) => Write(message, LogLevel.Info, details: details);

    /// <summary>Recoverable problems that fell back to a working state (unreadable file, failed reload, ...). Returns the created entry.</summary>
    public LogEntry Warn(string message) => Write(message, LogLevel.Warning);

    /// <summary>Recoverable problem with sub-lines shown below it in the console. Returns the created entry.</summary>
    public LogEntry Warn(string message, IReadOnlyList<string> details) => Write(message, LogLevel.Warning, details: details);

    /// <summary>Failures that break a feature (reserved for the severe paths). Returns the created entry.</summary>
    public LogEntry Error(string message) => Write(message, LogLevel.Error);

    /// <summary>Failure with sub-lines shown below it in the console. Returns the created entry.</summary>
    public LogEntry Error(string message, IReadOnlyList<string> details) => Write(message, LogLevel.Error, details: details);

    /// <summary>
    /// Logs an error with an exception. The exception's <c>ToString()</c>
    /// (message + stack trace) is captured as the entry's stack trace.
    /// Returns the created entry.
    /// </summary>
    public LogEntry Error(string message, Exception exception) => Write(message, LogLevel.Error, exception.ToString(), exception);

    /// <summary>
    /// Empties the buffered entries. The event history is not reset;
    /// new entries after a clear are still emitted normally.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
            _entries.Clear();
    }

    private LogEntry Write(string message, LogLevel level, string? stackTrace = null, Exception? exception = null,
        IReadOnlyList<string>? details = null)
    {
        // Always write to the terminal as a fallback, so headless runs
        // (tests, CI) and early startup before the console UI is loaded
        // still produce visible output.
        Console.WriteLine(message);
        if (details is not null)
            foreach (var line in details)
                Console.WriteLine(line);
        if (stackTrace is { Length: > 0 })
            Console.WriteLine(stackTrace);

        var entry = new LogEntry(message, level, DateTime.Now, stackTrace, exception)
        {
            Details = details
        };

        lock (_lock)
        {
            _entries.Add(entry);
            while (_entries.Count > MaxBufferedEntries)
                _entries.RemoveAt(0);
        }

        EntryAdded?.Invoke(entry);
        return entry;
    }
}