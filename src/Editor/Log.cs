namespace Crowbar.Editor;

/// <summary>
/// The editor's logging API — the s&amp;box-style <c>Log</c>, equivalent of the
/// console. Editor code reports progress and failures through
/// <see cref="Info"/>/<see cref="Warn"/>/<see cref="Error"/> instead of writing
/// to the console directly, so a future logger can intercept, colorize or file
/// the output without touching call sites. The messages carry their own domain
/// tags (<c>[Project]</c>, <c>[Level]</c>, <c>[Scripting]</c>, ...).
/// </summary>
public static class Log
{
    /// <summary>Normal progress messages (project, level, script lifecycle).</summary>
    public static void Info(string message) => Write(message);

    /// <summary>Recoverable problems that fell back to a working state (unreadable file, failed reload, ...).</summary>
    public static void Warn(string message) => Write(message);

    /// <summary>Failures that break a feature (reserved for the severe paths).</summary>
    public static void Error(string message) => Write(message);

    private static void Write(string message) => Console.WriteLine(message);
}
