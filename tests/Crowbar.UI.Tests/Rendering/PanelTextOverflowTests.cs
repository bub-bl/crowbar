using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// When a docked panel is narrowed, its content must clip instead of spilling
/// into the neighbouring pane (the viewport). Regression for the fixed-width
/// tree rows and search box that overflowed the Explorer panel.
/// </summary>
public class PanelTextOverflowTests
{
    [Fact]
    public void ExplorerContentStaysInsideItsPaneWhenNarrowed()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;

        var tree = TestUi.Find(content, p => p.Classes.Contains("tree"));
        Assert.NotNull(tree);
        var pane = Ancestor(tree!, p => p.Classes.Contains("dock-pane"));
        Assert.NotNull(pane);
        var initialWidth = pane!.Layout.Width;

        // Narrow the Explorer group by dragging the explorer|centre seam left.
        var splitter = TestUi.FindAll(content, p => p.Classes.Contains("dock-splitter-v"))[0];
        var startX = splitter.Layout.X + splitter.Layout.Width / 2;
        var startY = splitter.Layout.Y + splitter.Layout.Height / 2;

        ui.ProcessPointerDown(startX, startY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerMove(startX - 120, startY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerUp(startX - 120, startY);
        ui.Update();
        ui.Prepare();

        // Re-resolve after the resize (the dock rebuilds its panels).
        tree = TestUi.Find(ui.Content!, p => p.Classes.Contains("tree"));
        pane = Ancestor(tree!, p => p.Classes.Contains("dock-pane"));
        Assert.NotNull(pane);
        Assert.True(pane!.Layout.Width < initialWidth, "the explorer pane should actually be narrowed");

        // The tree, its search box, its rows and their labels all stay inside
        // the pane — nothing may extend past its right edge into the viewport.
        Assert.True(tree!.Layout.Right <= pane.Layout.Right + 1f, "the tree extends past the explorer pane");

        var search = TestUi.Find(ui.Content!, p => p.Classes.Contains("search") && !p.Classes.Contains("small"));
        Assert.NotNull(search);
        Assert.True(search!.Layout.Right <= pane.Layout.Right + 1f, "the search box extends past the explorer pane");

        foreach (var row in TestUi.FindAll(tree, p => p.Classes.Contains("tree-row")))
        {
            Assert.True(row.Layout.Right <= tree.Layout.Right + 1f,
                $"tree row extends past the tree ({row.Layout.Right} > {tree.Layout.Right})");

            var label = row.Children.Single(c => c.Classes.Contains("tree-text"));
            Assert.True(label.Layout.Right <= row.Layout.Right + 1f,
                $"tree label extends past its row ({label.Layout.Right} > {row.Layout.Right})");

            // The tree uses a hard clip (overflow: hidden), not an ellipsis.
            var textNode = label.Children.Single(c => c.TagName == "text");
            Assert.Equal("clip", textNode.ComputedStyle.TextOverflow);
        }

        // The tab strip clips its tabs the same way.
        foreach (var tab in TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-tab")))
        {
            Assert.True(tab.Parent is not null, "dock tab has no tab bar");
            Assert.True(tab.Layout.Right <= tab.Parent!.Layout.Right + 1f,
                $"dock tab extends past its tab bar ({tab.Layout.Right} > {tab.Parent!.Layout.Right})");
        }
    }

    private static Panel? Ancestor(Panel panel, Func<Panel, bool> predicate)
    {
        for (var p = panel; p is not null; p = p.Parent)
            if (predicate(p)) return p;
        return null;
    }
}
