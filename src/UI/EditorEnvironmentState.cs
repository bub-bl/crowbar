using System.Collections.Concurrent;
using System.Numerics;

namespace Crowbar.UI;

public static class EditorEnvironmentState
{
    public readonly record struct Snapshot(
        string Provider,
        string SourcePath,
        float Rotation,
        float Intensity,
        float Exposure,
        Vector4 Tint,
        string Status,
        string Diagnostic);

    public readonly record struct Edit(string Property, string Value);

    private static readonly ConcurrentQueue<Edit> Edits = new();
    private static int _pickSourceRequested;

    public static Snapshot Current { get; private set; } = new(
        "None", string.Empty, 0f, 1f, 0f, Vector4.One, "Empty", string.Empty);
    public static int Version { get; private set; }

    public static void Publish(Snapshot snapshot)
    {
        if (snapshot == Current)
            return;
        Current = snapshot;
        Version++;
    }

    public static void RequestEdit(string property, string value) =>
        Edits.Enqueue(new Edit(property, value));

    public static IReadOnlyList<Edit> ConsumeEdits()
    {
        var result = new List<Edit>();
        while (Edits.TryDequeue(out var edit))
            result.Add(edit);
        return result;
    }

    public static void RequestSourcePicker() => Interlocked.Exchange(ref _pickSourceRequested, 1);
    public static bool ConsumeSourcePickerRequest() => Interlocked.Exchange(ref _pickSourceRequested, 0) != 0;
}
