using System.Text;

namespace Crowbar.UI;

/// <summary>
/// Mirror of the selected entity's data that the Inspector panel displays.
/// The host republishes the snapshot every frame through <see cref="Publish"/>;
/// the panel is UI-only and never touches engine types, exactly like
/// <see cref="EditorExplorerState"/>. It also carries the UI → host collapse
/// choices, so the sections the user folded survive republishing.
/// </summary>
public static class EditorInspectorState
{
    /// <summary>
    /// One resolved property of the selected entity. <see cref="TypeName"/> is
    /// the canonical CLR type name of the value (System.Single,
    /// System.Numerics.Vector3, …) and is resolved to an editor component by
    /// the property editor registry. <see cref="Value"/> is the single
    /// serialized display value (a vector is a comma-separated string such as
    /// "0, 1, 2", which its editor splits). <see cref="Indent"/> nests a
    /// property under a composite parent (e.g. a material parameter under its
    /// material). <see cref="Key"/> is the stable write-back identity the host
    /// uses to apply an edit to the entity; it is empty for read-only rows.
    /// </summary>
    public readonly record struct Property(
        string Name,
        string TypeName,
        string Value,
        int Indent = 0,
        string Key = "");

    /// <summary>A collapsible inspector block: the transform or a single component.</summary>
    public readonly record struct Section(string Id, string Title, string? Icon, IReadOnlyList<Property> Properties);

    private static string _entityName = string.Empty;
    private static bool _hasSelection;
    private static IReadOnlyList<Section> _sections = [];
    private static string _signature = string.Empty;
    private static int _version;

    /// <summary>Name of the selected entity, or empty when nothing is selected.</summary>
    public static string EntityName => _entityName;

    /// <summary>True when an entity is selected and the panel shows its data.</summary>
    public static bool HasSelection => _hasSelection;

    /// <summary>Collapsible sections of the selected entity, in display order.</summary>
    public static IReadOnlyList<Section> Sections => _sections;

    /// <summary>Bumped whenever the snapshot or the collapse set changes.</summary>
    public static int Version => _version;

    private static readonly HashSet<string> Collapsed = new(StringComparer.Ordinal);
    private static readonly object EditLock = new();
    private static readonly List<(string Key, string Value)> PendingEdits = [];

    public static bool IsCollapsed(string id) => Collapsed.Contains(id);

    public static void Toggle(string id)
    {
        if (string.IsNullOrEmpty(id))
            return;
        if (!Collapsed.Add(id))
            Collapsed.Remove(id);
        _version++;
    }

    /// <summary>
    /// Clears the snapshot, the collapsed sections and the pending edits. Used
    /// by tests so each editor-page fixture starts from a clean slate (section
    /// ids are stable strings, unlike the explorer's per-publish guids).
    /// </summary>
    internal static void Reset()
    {
        Collapsed.Clear();
        lock (EditLock) PendingEdits.Clear();
        _entityName = string.Empty;
        _hasSelection = false;
        _sections = [];
        _signature = string.Empty;
        _version++;
    }

    /// <summary>Queues one value edit for the host to apply to the selected entity.</summary>
    public static void RequestEdit(string key, string value)
    {
        if (string.IsNullOrEmpty(key))
            return;
        lock (EditLock)
        {
            PendingEdits.Add((key, value));
        }
    }

    /// <summary>Returns and clears the edits queued since the previous call.</summary>
    public static IReadOnlyList<(string Key, string Value)> ConsumeEdits()
    {
        lock (EditLock)
        {
            if (PendingEdits.Count == 0)
                return [];
            var edits = PendingEdits.ToArray();
            PendingEdits.Clear();
            return edits;
        }
    }

    /// <summary>
    /// Replaces the snapshot. A no-op when nothing changed — the host publishes
    /// every frame, so identical frames must not force the panel to rebuild.
    /// Passing a null name clears the selection.
    /// </summary>
    public static void Publish(string? entityName, IReadOnlyList<Section> sections)
    {
        var hasSelection = entityName is not null;
        entityName ??= string.Empty;
        sections ??= [];

        var signature = BuildSignature(hasSelection, entityName, sections);
        if (string.Equals(signature, _signature, StringComparison.Ordinal))
            return;

        _signature = signature;
        _hasSelection = hasSelection;
        _entityName = entityName;
        _sections = sections;
        _version++;
    }

    /// <summary>
    /// Flattens the snapshot into a stable string so <see cref="Publish"/> can
    /// compare it to the previous frame without requiring the record structs to
    /// implement deep equality over their property lists.
    /// </summary>
    private static string BuildSignature(bool hasSelection, string entityName, IReadOnlyList<Section> sections)
    {
        var builder = new StringBuilder();
        builder.Append(hasSelection ? '1' : '0').Append('|').Append(entityName);
        foreach (var section in sections)
        {
            builder.Append('\n').Append(section.Id).Append('\u0001').Append(section.Title).Append('\u0001')
                .Append(section.Icon ?? string.Empty);
            foreach (var property in section.Properties)
                builder.Append('\u0002').Append(property.Name).Append('\u0003').Append(property.TypeName).Append('\u0003')
                    .Append(property.Value).Append('\u0003').Append(property.Indent).Append('\u0003').Append(property.Key);
        }

        return builder.ToString();
    }
}
