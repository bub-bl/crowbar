namespace Crowbar.Engine.Global;

/// <summary>
/// The logging API — the s&amp;box-style <c>Log</c>, equivalent of the console.
/// Exposed globally as <see cref="GlobalNamespaces.Log"/>; editor and game code
/// report progress and failures through <see cref="Info"/>/<see cref="Warn"/>/
/// <see cref="Error"/> instead of writing to the console directly, so a future
/// logger can intercept, colorize or file the output without touching call
/// sites. The messages carry their own domain tags (<c>[Project]</c>,
/// <c>[Level]</c>, <c>[Scripting]</c>, ...).
/// </summary>
public sealed class Log
{
    /// <summary>Normal progress messages (project, level, script lifecycle).</summary>
    public void Info(string message) => Write(message);

    /// <summary>Recoverable problems that fell back to a working state (unreadable file, failed reload, ...).</summary>
    public void Warn(string message) => Write(message);

    /// <summary>Failures that break a feature (reserved for the severe paths).</summary>
    public void Error(string message) => Write(message);

    private static void Write(string message) => Console.WriteLine(message);
}
