using Crowbar.UI.Docking;

namespace Crowbar.UI.Tests.Docking;

public class DockTreeTests
{
    /// <summary>Horizontal split of a fixed 100px group (a) and a fixed 200px group (b, c).</summary>
    private static DockTree SimpleTree()
    {
        var left = new DockGroupNode("left");
        left.Tabs.Add("a");
        var right = new DockGroupNode("right");
        right.Tabs.Add("b");
        right.Tabs.Add("c");
        right.Active = 1;
        var root = new DockSplitNode(horizontal: true);
        root.Children.Add(left);
        root.Weights.Add(100f);
        root.Children.Add(right);
        root.Weights.Add(200f);
        return new DockTree(root);
    }

    [Fact]
    public void GroupOfAndFindGroupResolveDockedItems()
    {
        var tree = SimpleTree();
        Assert.Equal("left", tree.GroupOf("a")?.Id);
        Assert.Equal("right", tree.GroupOf("b")?.Id);
        Assert.Null(tree.GroupOf("missing"));
        Assert.Equal("right", tree.FindGroup("right")?.Id);
        Assert.Null(tree.FindGroup("nope"));
        Assert.Equal("c", tree.GroupOf("b")?.ActiveTab);
    }

    [Fact]
    public void RemoveItemCollapsesEmptyGroupsAndSplits()
    {
        var tree = SimpleTree();
        tree.RemoveItem("b");
        tree.RemoveItem("c");
        // The right group is empty and detached; the root split keeps a single
        // child and is replaced by it.
        Assert.IsType<DockGroupNode>(tree.Root);
        Assert.Equal("left", ((DockGroupNode)tree.Root).Id);
        Assert.Null(tree.GroupOf("b"));
        Assert.NotNull(tree.GroupOf("a"));
    }

    [Fact]
    public void RemoveItemClampsActiveIndex()
    {
        var tree = SimpleTree();
        tree.RemoveItem("c"); // active was 1 (c), now only b remains
        Assert.Equal("b", tree.GroupOf("b")?.ActiveTab);
    }

    [Fact]
    public void DockToTabMovesItemBetweenGroupsAndActivates()
    {
        var tree = SimpleTree();
        tree.DockToTab("a", "right");
        Assert.Equal("right", tree.GroupOf("a")?.Id);
        var right = tree.FindGroup("right")!;
        Assert.Equal(new[] { "b", "c", "a" }, right.Tabs);
        Assert.Equal("a", right.ActiveTab);
        Assert.Null(tree.FindGroup("left")); // emptied by the move, collapsed
    }

    [Fact]
    public void MoveTabReordersTabsAndActivatesTheMovedTab()
    {
        var tree = SimpleTree();
        tree.MoveTab("c", "right", 0);

        var right = tree.FindGroup("right")!;
        Assert.Equal(new[] { "c", "b" }, right.Tabs);
        Assert.Equal("c", right.ActiveTab);
        Assert.Equal("right", tree.GroupOf("c")?.Id);
    }

    [Fact]
    public void MoveTabAdjustsInsertionWhenMovingForwardWithinTheSameGroup()
    {
        var tree = SimpleTree();
        tree.MoveTab("b", "right", 2); // Drop after c, using the pre-move slot.

        var right = tree.FindGroup("right")!;
        Assert.Equal(new[] { "c", "b" }, right.Tabs);
        Assert.Equal("b", right.ActiveTab);
    }

    [Fact]
    public void DockToSplitWrapsTargetWithItemOnRequestedSide()
    {
        var tree = SimpleTree();
        tree.DockToSplit("a", "right", DockZone.Left);
        var root = Assert.IsType<DockSplitNode>(tree.Root);
        Assert.True(root.Horizontal);
        Assert.Equal(2, root.Children.Count);
        var fresh = Assert.IsType<DockGroupNode>(root.Children[0]);
        Assert.Contains("a", fresh.Tabs);
        Assert.Same(tree.FindGroup("right"), root.Children[1]);
    }

    [Fact]
    public void DockToSplitOnRightSidePlacesFreshGroupAfterTarget()
    {
        var tree = SimpleTree();
        tree.DockToSplit("a", "right", DockZone.Right);
        var root = Assert.IsType<DockSplitNode>(tree.Root);
        Assert.Same(tree.FindGroup("right"), root.Children[0]);
        var fresh = Assert.IsType<DockGroupNode>(root.Children[1]);
        Assert.Contains("a", fresh.Tabs);
    }

    [Fact]
    public void DockToSplitVerticalZoneUsesVerticalSplit()
    {
        var tree = SimpleTree();
        tree.DockToSplit("a", "right", DockZone.Top);
        var root = Assert.IsType<DockSplitNode>(tree.Root);
        Assert.False(root.Horizontal);
        Assert.Contains("a", Assert.IsType<DockGroupNode>(root.Children[0]).Tabs);
    }

    [Fact]
    public void DockToSplitOfLastTabIntoSameGroupNeverLosesTheItem()
    {
        // 'a' is the only tab of its group; docking it to an edge of that same
        // group collapses the target, so the item must be parked, not lost.
        var tree = SimpleTree();
        tree.DockToSplit("a", "left", DockZone.Left);
        Assert.NotNull(tree.GroupOf("a"));
    }

    [Fact]
    public void DockToTabOfLastTabIntoSameGroupStaysDocked()
    {
        var tree = SimpleTree();
        tree.DockToTab("a", "left");
        Assert.NotNull(tree.GroupOf("a"));
    }

    [Fact]
    public void ComputeRectsScalesFixedChildrenToFillTheArea()
    {
        var tree = SimpleTree(); // fixed 100 + 200, 1px seam
        var rects = new Dictionary<string, UiRect>();
        tree.ComputeRects(new UiRect(0, 0, 400, 300), rects);
        Assert.Equal(133f, rects["left"].Width, precision: 1);
        Assert.Equal(266f, rects["right"].Width, precision: 1);
        Assert.Equal(134f, rects["right"].X, precision: 1); // left width + seam
        Assert.Equal(0f, rects["left"].Y);
        Assert.Equal(300f, rects["left"].Height);
    }

    [Fact]
    public void FlexWeightsShareRemainingSpaceAfterFixed()
    {
        var tools = new DockGroupNode("tools");
        tools.Tabs.Add("t");
        var flex = new DockGroupNode("flex");
        flex.Tabs.Add("f");
        var root = new DockSplitNode(horizontal: true);
        root.Children.Add(tools);
        root.Weights.Add(100f);
        root.Children.Add(flex);
        root.Weights.Add(0f);
        var tree = new DockTree(root);
        var rects = new Dictionary<string, UiRect>();
        tree.ComputeRects(new UiRect(0, 0, 401, 200), rects);
        Assert.Equal(100f, rects["tools"].Width);
        Assert.Equal(300f, rects["flex"].Width); // 401 - 100 - 1px seam
        Assert.Equal(101f, rects["flex"].X);
    }

    [Fact]
    public void ComputeSplittersPlacesHandleOnSeam()
    {
        var left = new DockGroupNode("l");
        left.Tabs.Add("a");
        var right = new DockGroupNode("r");
        right.Tabs.Add("b");
        var root = new DockSplitNode(horizontal: true);
        root.Children.Add(left);
        root.Weights.Add(0f);
        root.Children.Add(right);
        root.Weights.Add(0f);
        var tree = new DockTree(root);
        var splitters = new List<DockSplitter>();
        tree.ComputeSplitters(new UiRect(0, 0, 401, 300), splitters);
        var splitter = Assert.Single(splitters);
        Assert.True(splitter.Horizontal); // horizontal split, vertical handle
        Assert.Equal(199f, splitter.Rect.X, precision: 1); // seam at 200, centered
        Assert.Equal(3f, splitter.Rect.Width, precision: 1);
        Assert.Equal(300f, splitter.Rect.Height);
        Assert.Equal(root.Id, splitter.SplitId);
        Assert.Equal(0, splitter.Index);
    }
}
