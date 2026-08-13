namespace Crowbar.UI.Docking;

/// <summary>
/// Where a docked item lands relative to a target group. <see cref="Center"/>
/// adds the item as a tab of the group; the edge zones wrap the group in a new
/// split with the item on the requested side.
/// </summary>
public enum DockZone
{
    Center,
    Left,
    Right,
    Top,
    Bottom
}

/// <summary>A node of the dock layout tree: a tab group or a split.</summary>
public abstract class DockNode
{
}

/// <summary>
/// A tab group: an ordered list of docked item ids with one active tab. The
/// id is the group's identity in the layout (used to key pixel rects and drop
/// targets); it is independent of the ids of the tabs it hosts.
/// </summary>
public sealed class DockGroupNode : DockNode
{
    public DockGroupNode(string id)
    {
        Id = id;
    }

    /// <summary>Stable identity of the group, unique within a tree.</summary>
    public string Id { get; }

    /// <summary>Docked item ids, in tab order.</summary>
    public List<string> Tabs { get; } = new();

    /// <summary>Index of the active tab, clamped to the tab list.</summary>
    public int Active { get; set; }

    /// <summary>The id of the active tab, or empty when the group has no tabs.</summary>
    public string ActiveTab => Tabs.Count == 0 ? string.Empty : Tabs[Math.Clamp(Active, 0, Tabs.Count - 1)];

    public bool Contains(string itemId) => Tabs.Contains(itemId);
}

/// <summary>
/// A split: children arranged in a row (<see cref="Horizontal"/>) or a column,
/// sized by per-child weights. A weight &gt; 0 is a fixed pixel size; a weight
/// &lt;= 0 is flexible and shares the remaining space equally with the other
/// flexible children.
/// </summary>
public sealed class DockSplitNode : DockNode
{
    public DockSplitNode(bool horizontal)
    {
        Horizontal = horizontal;
    }

    /// <summary>Identity used by the splitter handles rendered over the seam.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public bool Horizontal { get; }

    public List<DockNode> Children { get; } = new();

    public List<float> Weights { get; } = new();
}

/// <summary>A draggable divider rendered over the seam between two split children.</summary>
public readonly record struct DockSplitter(string SplitId, int Index, bool Horizontal, UiRect Rect);

/// <summary>
/// The dock layout tree and its operations (tab docking, split docking, item
/// removal with split collapse) plus the pixel-rect resolution used both to
/// position the groups and to hit-test drop zones. Layout-agnostic: rects are
/// computed for an arbitrary area rect, so the host component can pass the
/// current size of the dock area.
/// </summary>
public sealed class DockTree
{
    /// <summary>Width of the 1px seam left between adjacent split children.</summary>
    public const float SplitterGap = 1f;

    /// <summary>Smallest edge-zone fraction used to classify drop zones.</summary>
    public const float EdgeZoneFraction = 0.22f;

    public DockTree(DockNode root)
    {
        Root = root;
    }

    public DockNode Root { get; set; }

    /// <summary>Every group of the tree, depth-first.</summary>
    public IEnumerable<DockGroupNode> Groups()
    {
        foreach (var node in Nodes())
            if (node is DockGroupNode group)
                yield return group;
    }

    /// <summary>Every node of the tree, depth-first.</summary>
    public IEnumerable<DockNode> Nodes()
    {
        var stack = new Stack<DockNode>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            if (node is DockSplitNode split)
                for (var i = split.Children.Count - 1; i >= 0; i--)
                    stack.Push(split.Children[i]);
        }
    }

    /// <summary>Every split of the tree.</summary>
    public IEnumerable<DockSplitNode> Splits() => Nodes().OfType<DockSplitNode>();

    /// <summary>The group hosting <paramref name="itemId"/>, or null when the item is not docked.</summary>
    public DockGroupNode? GroupOf(string itemId) => Groups().FirstOrDefault(group => group.Contains(itemId));

    /// <summary>The group with the given id, or null.</summary>
    public DockGroupNode? FindGroup(string id) => Groups().FirstOrDefault(group => group.Id == id);

    /// <summary>
    /// Removes an item from its group; a group left empty is detached and the
    /// surrounding splits are collapsed (a split with a single child is
    /// replaced by that child, an empty split is removed).
    /// </summary>
    public void RemoveItem(string itemId)
    {
        var group = GroupOf(itemId);
        if (group is null) return;
        group.Tabs.Remove(itemId);
        if (group.Tabs.Count == 0) Detach(group);
        else group.Active = Math.Clamp(group.Active, 0, group.Tabs.Count - 1);
    }

    /// <summary>
    /// Moves an item to a specific tab slot. The target index is measured in
    /// the target group's order before the source item is removed; this makes
    /// drop-before/drop-after hit testing stable even when source and target
    /// are the same group.
    /// </summary>
    public void MoveTab(string itemId, string targetGroupId, int targetIndex)
    {
        var source = GroupOf(itemId);
        var target = FindGroup(targetGroupId);
        if (source is null || target is null)
        {
            if (source is null) ParkAtRoot(itemId);
            return;
        }

        var sourceIndex = source.Tabs.IndexOf(itemId);
        if (sourceIndex < 0) return;
        source.Tabs.RemoveAt(sourceIndex);
        if (ReferenceEquals(source, target) && sourceIndex < targetIndex) targetIndex--;

        if (source.Tabs.Count == 0) Detach(source);
        else source.Active = Math.Clamp(source.Active, 0, source.Tabs.Count - 1);

        target = FindGroup(targetGroupId);
        if (target is null)
        {
            ParkAtRoot(itemId);
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, target.Tabs.Count);
        target.Tabs.Insert(targetIndex, itemId);
        target.Active = targetIndex;
    }

    /// <summary>Docks the item into the target group as a new (active) tab.</summary>
    public void DockToTab(string itemId, string targetGroupId)
    {
        var target = FindGroup(targetGroupId);
        if (target is null)
        {
            // The target group no longer exists: keep the item docked anyway.
            ParkAtRoot(itemId);
            return;
        }

        RemoveItem(itemId);
        // Removing the item may have collapsed the target itself when the item
        // was its only tab (source and target the same group): re-resolve so
        // the item is never docked into a detached group.
        var resolved = FindGroup(targetGroupId);
        if (resolved is not null)
        {
            resolved.Tabs.Add(itemId);
            resolved.Active = resolved.Tabs.Count - 1;
            return;
        }

        ParkAtRoot(itemId);
    }

    /// <summary>
    /// Docks the item on an edge of the target group: the group is wrapped in
    /// a new split with a fresh group holding the item on the requested side.
    /// When the item was the target's only tab (the group collapses during the
    /// move), the item is parked as a tab instead so it is never lost.
    /// </summary>
    public void DockToSplit(string itemId, string targetGroupId, DockZone zone)
    {
        var target = FindGroup(targetGroupId);
        if (target is null) return;
        RemoveItem(itemId);
        target = FindGroup(targetGroupId);
        if (target is null)
        {
            // The target collapsed because the item was its only tab; there is
            // nothing left to split around. Keep the item docked as a tab.
            DockToTab(itemId, targetGroupId);
            return;
        }

        var fresh = new DockGroupNode(NewGroupId());
        fresh.Tabs.Add(itemId);
        fresh.Active = 0;
        var horizontal = zone is DockZone.Left or DockZone.Right;
        var targetFirst = zone is DockZone.Right or DockZone.Bottom;
        var split = new DockSplitNode(horizontal);
        split.Children.Add(targetFirst ? target : fresh);
        split.Children.Add(targetFirst ? fresh : target);
        split.Weights.Add(0f);
        split.Weights.Add(0f);
        Replace(target, split);
    }

    /// <summary>
    /// Docks the item into a fresh group so it is never lost when its previous
    /// group (and the requested target) collapsed during the move. The group
    /// is added to the root split, or the root is wrapped when it is a single
    /// group. The unique group id keeps the fresh group distinct from any
    /// remaining group with the same item.
    /// </summary>
    public void ParkAtRoot(string itemId)
    {
        if (Root is DockGroupNode rootGroup && rootGroup.Tabs.Count == 0)
        {
            // The root is an empty placeholder left by a collapse: reuse it.
            rootGroup.Tabs.Add(itemId);
            rootGroup.Active = 0;
            return;
        }

        var group = new DockGroupNode(NewGroupId());
        group.Tabs.Add(itemId);
        group.Active = 0;
        if (Root is DockSplitNode rootSplit)
        {
            rootSplit.Children.Add(group);
            rootSplit.Weights.Add(0f);
            return;
        }

        var split = new DockSplitNode(horizontal: true);
        split.Children.Add(Root);
        split.Children.Add(group);
        split.Weights.Add(0f);
        split.Weights.Add(0f);
        Root = split;
    }

    private static int _groupCounter;

    private static string NewGroupId() => "g" + Interlocked.Increment(ref _groupCounter).ToString("X");

    /// <summary>
    /// Computes the pixel rect of every group for a dock area of the given
    /// size, depth-first. Rects are expressed in the same coordinate space as
    /// <paramref name="area"/>.
    /// </summary>
    public void ComputeRects(UiRect area, Dictionary<string, UiRect> rects)
    {
        rects.Clear();
        LayoutNode(Root, area, rects);
    }

    /// <summary>Rects of the direct children of <paramref name="split"/>, resolved for the given area.</summary>
    public List<UiRect> SplitChildRects(DockSplitNode split, UiRect area)
    {
        var result = new List<UiRect>();
        var splitRect = NodeRect(split, area);
        if (splitRect is null) return result;
        var rect = splitRect.Value;
        var weights = ResolveWeights(split, rect);
        var offset = split.Horizontal ? rect.X : rect.Y;
        foreach (var weight in weights)
        {
            result.Add(split.Horizontal
                ? new UiRect(offset, rect.Y, weight, rect.Height)
                : new UiRect(rect.X, offset, rect.Width, weight));
            offset += weight + SplitterGap;
        }

        return result;
    }

    /// <summary>
    /// Computes the draggable splitter handles of every split for a dock area
    /// of the given size: a 3px strip centered on each 1px seam between two
    /// adjacent children, spanning the split's cross axis.
    /// </summary>
    public void ComputeSplitters(UiRect area, List<DockSplitter> splitters)
    {
        splitters.Clear();
        CollectSplitters(Root, area, splitters);
    }

    /// <summary>The rect of <paramref name="target"/> within the area, or null when not reachable.</summary>
    public UiRect? NodeRect(DockNode target, UiRect area)
    {
        return Walk(Root, area);

        UiRect? Walk(DockNode node, UiRect rect)
        {
            if (ReferenceEquals(node, target)) return rect;
            if (node is not DockSplitNode split) return null;
            var weights = ResolveWeights(split, rect);
            var offset = split.Horizontal ? rect.X : rect.Y;
            for (var i = 0; i < split.Children.Count; i++)
            {
                var childRect = split.Horizontal
                    ? new UiRect(offset, rect.Y, weights[i], rect.Height)
                    : new UiRect(rect.X, offset, rect.Width, weights[i]);
                if (Walk(split.Children[i], childRect) is { } found) return found;
                offset += weights[i] + SplitterGap;
            }

            return null;
        }
    }

    /// <summary>Resolved pixel sizes of the split's children for the given rect.</summary>
    internal float[] ResolveWeights(DockSplitNode split, UiRect rect)
    {
        var count = split.Children.Count;
        var result = new float[count];
        if (count == 0) return result;
        var available = split.Horizontal ? rect.Width : rect.Height;
        var fixedTotal = 0f;
        var flexCount = 0;
        for (var i = 0; i < count; i++)
        {
            var weight = split.Weights[i];
            if (weight > 0f) fixedTotal += weight;
            else flexCount++;
        }

        var gaps = SplitterGap * (count - 1);
        var remaining = Math.Max(0f, available - gaps - fixedTotal);
        var flexShare = flexCount > 0 ? remaining / flexCount : 0f;
        for (var i = 0; i < count; i++)
        {
            var weight = split.Weights[i];
            result[i] = weight > 0f ? weight : flexShare;
        }

        // Without a flexible child the fixed sizes are proportions: scale them
        // to fill the available space exactly (both directions, so resizing the
        // window keeps the panes edge-to-edge).
        if (flexCount == 0 && fixedTotal > 0)
        {
            var factor = Math.Max(0.05f, (available - gaps) / fixedTotal);
            for (var i = 0; i < count; i++) result[i] *= factor;
        }

        return result;
    }

    private void LayoutNode(DockNode node, UiRect rect, Dictionary<string, UiRect> rects)
    {
        if (node is DockGroupNode group)
        {
            rects[group.Id] = rect;
            return;
        }

        if (node is not DockSplitNode split || split.Children.Count == 0) return;
        var weights = ResolveWeights(split, rect);
        var offset = split.Horizontal ? rect.X : rect.Y;
        for (var i = 0; i < split.Children.Count; i++)
        {
            var childRect = split.Horizontal
                ? new UiRect(offset, rect.Y, weights[i], rect.Height)
                : new UiRect(rect.X, offset, rect.Width, weights[i]);
            LayoutNode(split.Children[i], childRect, rects);
            offset += weights[i] + SplitterGap;
        }
    }

    private void CollectSplitters(DockNode node, UiRect rect, List<DockSplitter> splitters)
    {
        if (node is not DockSplitNode split || split.Children.Count < 2) return;
        var weights = ResolveWeights(split, rect);
        var offset = split.Horizontal ? rect.X : rect.Y;
        for (var i = 0; i < split.Children.Count; i++)
        {
            var childRect = split.Horizontal
                ? new UiRect(offset, rect.Y, weights[i], rect.Height)
                : new UiRect(rect.X, offset, rect.Width, weights[i]);
            CollectSplitters(split.Children[i], childRect, splitters);
            if (i < split.Children.Count - 1)
            {
                var seam = offset + weights[i];
                var splitterRect = split.Horizontal
                    ? new UiRect(seam - 1, rect.Y, SplitterGap + 2, rect.Height)
                    : new UiRect(rect.X, seam - 1, rect.Width, SplitterGap + 2);
                splitters.Add(new DockSplitter(split.Id, i, split.Horizontal, splitterRect));
            }

            offset += weights[i] + SplitterGap;
        }
    }

    private void Detach(DockNode node)
    {
        if (ReferenceEquals(Root, node))
        {
            Root = new DockGroupNode("empty");
            return;
        }

        var parent = FindParent(node);
        if (parent is null) return;
        var index = parent.Children.IndexOf(node);
        if (index < 0) return;
        parent.Children.RemoveAt(index);
        parent.Weights.RemoveAt(index);
        Collapse(parent);
    }

    private void Collapse(DockSplitNode split)
    {
        if (split.Children.Count == 1)
        {
            var only = split.Children[0];
            Replace(split, only);
        }
        else if (split.Children.Count == 0)
        {
            Detach(split);
        }
    }

    private void Replace(DockNode old, DockNode fresh)
    {
        if (ReferenceEquals(Root, old))
        {
            Root = fresh;
            return;
        }

        var parent = FindParent(old);
        if (parent is null) return;
        var index = parent.Children.IndexOf(old);
        if (index < 0) return;
        parent.Children[index] = fresh;
    }

    private DockSplitNode? FindParent(DockNode node)
    {
        foreach (var split in Splits())
            if (split.Children.Contains(node))
                return split;
        return null;
    }
}
