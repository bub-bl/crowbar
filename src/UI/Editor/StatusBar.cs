namespace Crowbar.Editor;

/// <summary>A single entry in the editor's status bar: a label and its live value.</summary>
public sealed record StatusBarEntry(string Label, string? Value);

/// <summary>
/// The editor's status bar, as seen by game code. Any code — a game component, a
/// system, a tool — can add, update or remove entries; the editor renders them
/// live. This is the editor's public API surface for games: it lives in a
/// shared assembly the game project references, so the project can publish status
/// without the editor ever referencing the game project.
///
/// Entries are (re)established by game code whenever it runs: components
/// register theirs when attached to an entity and remove them when destroyed.
/// The editor clears the bar when (re)loading the game project and the script
/// host before a full hot reload, so only the live project's registrations
/// survive. Producers are evaluated on every snapshot; a throwing producer shows
/// "(erreur de script)" instead.
/// </summary>
public static class StatusBar
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Func<string?>> Entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds or replaces the entry with the given <paramref name="label"/>. The
    /// producer is evaluated whenever the editor snapshots the status bar, so
    /// the displayed value stays live (e.g. game state changing over time).
    /// </summary>
    public static void AddEntry(string label, Func<string?> value)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(value);
        lock (Gate)
            Entries[label] = value;
    }

    /// <summary>Adds or replaces the entry with a fixed <paramref name="value"/>.</summary>
    public static void AddEntry(string label, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        AddEntry(label, () => value);
    }

    /// <summary>Removes the entry with the given label, if present.</summary>
    public static void RemoveEntry(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        lock (Gate)
            Entries.Remove(label);
    }

    /// <summary>
    /// Removes every entry. The editor calls it when (re)loading the game
    /// project and the script host before a full hot reload, so only the live
    /// project's registrations survive.
    /// </summary>
    public static void Clear()
    {
        lock (Gate)
            Entries.Clear();
    }

    /// <summary>
    /// Evaluates every entry's producer and returns the current label/value
    /// pairs in insertion order. The editor reads this every frame.
    /// </summary>
    public static IReadOnlyList<StatusBarEntry> Snapshot()
    {
        KeyValuePair<string, Func<string?>>[] entries;
        lock (Gate)
            entries = Entries.ToArray();

        var snapshot = new StatusBarEntry[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            string? value;
            try
            {
                value = entries[i].Value();
            }
            catch (Exception)
            {
                value = "(erreur de script)";
            }

            snapshot[i] = new StatusBarEntry(entries[i].Key, value);
        }

        return snapshot;
    }
}