namespace Crowbar.UI;

/// <summary>
/// Mirror of the live world hierarchy that the Explorer panel displays. The
/// editor host owns the <c>World</c> and republishes the flat node list every
/// frame through <see cref="Publish"/>; the panel itself is UI-only and never
/// touches engine types, keeping the UI assembly a leaf (Engine → UI, like
/// <see cref="UiDiagnostics"/> and <see cref="GizmoToolState"/>). Also carries
/// the UI → host channels: entity selection requests from the panel (consumed
/// by the host each frame) and the user's collapsed folder set.
/// </summary>
public static class EditorExplorerState
{
    /// <summary>
    /// A single row of the Explorer tree. Children nest under their parent's
    /// <see cref="Id"/> (<see langword="null"/> for the world root).
    /// </summary>
    public readonly record struct TreeNode(string Name, Guid Id, Guid? ParentId, string Icon, bool IsFolder);

    private static IReadOnlyList<TreeNode> _nodes = [];
    private static int _version;

    /// <summary>Flat pre-order rows of the world tree (folders and entities).</summary>
    public static IReadOnlyList<TreeNode> Nodes => _nodes;

    /// <summary>Id of the entity selected in the viewport (the gizmo target), or null.</summary>
    public static Guid? SelectionId { get; private set; }

    /// <summary>
    /// Bumped whenever the hierarchy, the selection or the collapse set changes;
    /// the Explorer panel hashes it so it re-renders only then.
    /// </summary>
    public static int Version => _version;

    /// <summary>Ids of the folders the user collapsed in the panel.</summary>
    private static readonly HashSet<Guid> Collapsed = [];

    public static bool IsCollapsed(Guid id) => Collapsed.Contains(id);

    public static void ToggleCollapsed(Guid id)
    {
        if (!Collapsed.Add(id)) Collapsed.Remove(id);
        _version++;
    }

    /// <summary>Selection requested by clicking an entity row (UI → host, consumed each frame).</summary>
    public static Guid? RequestedSelectionId { get; private set; }

    public static void RequestSelection(Guid id) => RequestedSelectionId = id;

    /// <summary>Returns and clears the pending selection request, or null.</summary>
    public static Guid? ConsumeRequestedSelection()
    {
        var requested = RequestedSelectionId;
        RequestedSelectionId = null;
        return requested;
    }

    /// <summary>
    /// Replaces the tree snapshot, bumping <see cref="Version"/> only when the
    /// hierarchy or the selection actually changed (the host publishes every
    /// frame, so a no-op must not force a panel rebuild).
    /// </summary>
    public static void Publish(IReadOnlyList<TreeNode> nodes, Guid? selectionId)
    {
        if (selectionId == SelectionId && nodes.SequenceEqual(_nodes)) return;
        _nodes = nodes;
        SelectionId = selectionId;
        _version++;
    }
}
