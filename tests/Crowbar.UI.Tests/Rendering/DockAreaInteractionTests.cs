using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// Exercises the DockArea's real interaction wiring on the rendered editor
/// page: grabbing a tab, dragging it onto a drop zone and releasing must
/// re-dock the panel (tab or split), exactly as the user would.
/// </summary>
public class DockAreaInteractionTests
{
    [Fact]
    public void DockAreaSurvivesUpdateBeforeItsFirstRender()
    {
        var uiDir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "src", "Editor", "Ui"));
        using var ui = new UiSystem();
        ui.SetViewport(1280, 720);
        ui.RegisterRazorComponentsFromDirectory(uiDir);
        ui.Navigate("/editor");

        // This is the production startup order: the first update happens
        // before the first render has assigned component layout rectangles.
        // Render must defer the geometry-dependent DockArea rebuild until after
        // that first layout pass; no second manual frame is required.
        ui.Update();
        ui.Prepare();

        Assert.NotNull(FindDockTab(ui.Content!, "HIERARCHY"));
        Assert.NotNull(FindDockTab(ui.Content!, "VIEWPORT"));
    }

    [Fact]
    public void DraggingTabToCenterOfAnotherGroupDocksItAsTab()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;

        // Grab the HIERARCHY tab and drag it onto the middle of the
        // VIEWPORT group (the center drop zone docks as a tab).
        var explorerTab = FindDockTab(content, "HIERARCHY");
        Assert.NotNull(explorerTab);
        var viewportGroup = TestUi.FindAll(content, p => p.Classes.Contains("dock-group"))
            .Single(group => TestUi.Texts(group).Any(text => text.Contains("VIEWPORT", StringComparison.Ordinal)));

        var grabX = explorerTab!.Layout.X + 5;
        var grabY = explorerTab.Layout.Y + 5;
        var targetX = viewportGroup.Layout.X + viewportGroup.Layout.Width / 2;
        var targetY = viewportGroup.Layout.Y + viewportGroup.Layout.Height / 2;

        ui.ProcessPointerDown(grabX, grabY);
        // The real app can render a frame immediately after the press. The
        // drag must survive that reconciliation before the pointer moves.
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerMove(targetX, targetY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerUp(targetX, targetY);
        ui.Update();
        ui.Prepare();

        // HIERARCHY is now a tab of the same group as VIEWPORT, and the
        // emptied explorer group collapsed out of the layout.
        var tabs = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-tab"));
        var explorer = Assert.Single(tabs, tab => TestUi.Texts(tab).Any(text => text.Contains("HIERARCHY", StringComparison.Ordinal)));
        var viewport = Assert.Single(tabs, tab => TestUi.Texts(tab).Any(text => text.Contains("VIEWPORT", StringComparison.Ordinal)));
        Assert.Same(explorer.Parent, viewport.Parent);
        Assert.Single(TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-tab") && TestUi.Texts(p).Any(t => t.Contains("HIERARCHY", StringComparison.Ordinal))));
        Assert.Contains(TestUi.Texts(ui.Content!), text => text.Contains("Search", StringComparison.Ordinal));
    }

    [Fact]
    public void DraggingTabToRightEdgeOfAnotherGroupSplitsIt()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;

        // Drag HIERARCHY onto the right edge of the VIEWPORT group: the
        // target group must be wrapped in a split with a fresh group holding
        // HIERARCHY on the right side.
        var explorerTab = FindDockTab(content, "HIERARCHY");
        Assert.NotNull(explorerTab);
        var viewportGroup = TestUi.FindAll(content, p => p.Classes.Contains("dock-group"))
            .Single(group => TestUi.Texts(group).Any(text => text.Contains("VIEWPORT", StringComparison.Ordinal)));

        var grabX = explorerTab!.Layout.X + 5;
        var grabY = explorerTab.Layout.Y + 5;
        var targetX = viewportGroup.Layout.Right - 8;
        var targetY = viewportGroup.Layout.Y + viewportGroup.Layout.Height / 2;

        ui.ProcessPointerDown(grabX, grabY);
        // The real app can render a frame immediately after the press. The
        // drag must survive that reconciliation before the pointer moves.
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerMove(targetX, targetY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerUp(targetX, targetY);
        ui.Update();
        ui.Prepare();

        // HIERARCHY sits in its own group now, next to (not inside) VIEWPORT.
        // The pane is identified by its tree: the CONTENT pane also carries a
        // small search box, so "Search" alone would match both.
        var explorerTabAfter = FindDockTab(ui.Content!, "HIERARCHY");
        Assert.NotNull(explorerTabAfter);
        Assert.NotSame(explorerTabAfter!.Parent, FindDockTab(ui.Content!, "VIEWPORT")?.Parent);
        var explorerPane = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-pane-active"))
            .Single(pane => TestUi.Find(pane, p => p.Classes.Contains("tree")) is not null);
        Assert.True(explorerPane.Layout.Width > 0);
        Assert.True(explorerPane.Layout.Height > 0);
        Assert.Contains(TestUi.Texts(ui.Content!), text => text.Contains("Search", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasingOutsideDockAreaCancelsDragWithoutLeavingGhost()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;
        var explorerTab = FindDockTab(content, "HIERARCHY");
        Assert.NotNull(explorerTab);
        var viewportGroup = TestUi.FindAll(content, p => p.Classes.Contains("dock-group"))
            .Single(group => TestUi.Texts(group).Any(text => text.Contains("VIEWPORT", StringComparison.Ordinal)));

        var grabX = explorerTab!.Layout.X + 5;
        var grabY = explorerTab.Layout.Y + 5;
        var targetX = viewportGroup.Layout.X + viewportGroup.Layout.Width / 2;
        var targetY = viewportGroup.Layout.Y + viewportGroup.Layout.Height / 2;

        ui.ProcessPointerDown(grabX, grabY);
        ui.ProcessPointerMove(targetX, targetY);
        ui.Update();
        ui.Prepare();
        // (10, 10) is in the fixed top bar, outside DockArea. Pointer capture
        // must still receive the release and clear the transient drag state.
        ui.ProcessPointerUp(10, 10);
        ui.Update();
        ui.Prepare();

        var tabs = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-tab"));
        Assert.NotNull(FindDockTab(ui.Content!, "HIERARCHY"));
        Assert.NotNull(FindDockTab(ui.Content!, "VIEWPORT"));
        Assert.Equal(4, tabs.Count);
        Assert.Empty(TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-ghost")));
    }

    /// <summary>
    /// Docks the tab titled <paramref name="dragged"/> into the group holding
    /// <paramref name="target"/> as a new active tab (the center drop zone).
    /// Returns a UI with a two-tab group, used by the reorder tests now that
    /// every initial dock group holds a single panel.
    /// </summary>
    private static UiSystem DockAsTab(UiSystem ui, string dragged, string target)
    {
        var content = ui.Content!;
        var draggedTab = FindDockTab(content, dragged);
        Assert.NotNull(draggedTab);
        var targetGroup = TestUi.FindAll(content, p => p.Classes.Contains("dock-group"))
            .Single(group => TestUi.Texts(group).Any(text => text.Contains(target, StringComparison.Ordinal)));

        var grabX = draggedTab!.Layout.X + 5;
        var grabY = draggedTab.Layout.Y + 5;
        var targetX = targetGroup.Layout.X + targetGroup.Layout.Width / 2;
        var targetY = targetGroup.Layout.Y + targetGroup.Layout.Height / 2;

        ui.ProcessPointerDown(grabX, grabY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerMove(targetX, targetY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerUp(targetX, targetY);
        ui.Update();
        ui.Prepare();
        return ui;
    }

    [Fact]
    public void MovingPointerWithoutDraggingDoesNotRebuildTheDockTree()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var before = ui.Renderer.LayoutPasses;

        // DockArea receives high-frequency mouse-move events even when no drag
        // is active. The handler must not schedule a Razor rebuild or Yoga pass
        // in that case.
        ui.ProcessPointerMove(200, 100);
        ui.Update();
        ui.Prepare();

        Assert.Equal(before, ui.Renderer.LayoutPasses);
    }

    [Fact]
    public void DraggingTabBeforeAnotherTabReordersTheGroup()
    {
        // The initial groups each hold a single panel now: build a two-tab
        // group by docking HIERARCHY into the VIEWPORT group.
        using var ui = DockAsTab(EditorPageCompositionTests.CreateEditorUi(), "HIERARCHY", "VIEWPORT");
        var content = ui.Content!;
        var viewportTab = FindDockTab(content, "VIEWPORT");
        var hierarchyTab = FindDockTab(content, "HIERARCHY");
        Assert.NotNull(viewportTab);
        Assert.NotNull(hierarchyTab);

        var grabX = hierarchyTab!.Layout.X + 5;
        var grabY = hierarchyTab.Layout.Y + 5;
        var targetX = viewportTab!.Layout.X + 1;
        var targetY = viewportTab.Layout.Y + 5;

        ui.ProcessPointerDown(grabX, grabY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerMove(targetX, targetY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerUp(targetX, targetY);
        ui.Update();
        ui.Prepare();

        var group = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-group"))
            .Single(group => TestUi.Texts(group).Contains("VIEWPORT") && TestUi.Texts(group).Contains("HIERARCHY"));
        var tabLabels = TestUi.FindAll(group, p => p.Classes.Contains("dock-tab"))
            .Select(tab => Assert.Single(TestUi.Texts(tab)))
            .ToArray();
        Assert.Equal(new[] { "HIERARCHY", "VIEWPORT" }, tabLabels);
        Assert.Contains(TestUi.FindAll(group, p => p.Classes.Contains("dock-tab-active")),
            tab => TestUi.Texts(tab).Contains("HIERARCHY"));
    }

    [Fact]
    public void DraggingTabSlidesNeighbouringTabsAsideAndSettlesOnRelease()
    {
        // Two-tab group: VIEWPORT then HIERARCHY (docked in), active on top.
        using var ui = DockAsTab(EditorPageCompositionTests.CreateEditorUi(), "HIERARCHY", "VIEWPORT");
        var content = ui.Content!;
        var viewportTab = FindDockTab(content, "VIEWPORT");
        var hierarchyTab = FindDockTab(content, "HIERARCHY");
        Assert.NotNull(viewportTab);
        Assert.NotNull(hierarchyTab);

        var grabX = hierarchyTab!.Layout.X + 5;
        var grabY = hierarchyTab.Layout.Y + 5;
        var targetX = viewportTab!.Layout.X + 1;
        var targetY = viewportTab.Layout.Y + 5;

        ui.ProcessPointerDown(grabX, grabY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerMove(targetX, targetY);
        ui.Update();
        ui.Prepare();
        // Let the 140ms slide/fade transitions run to completion so the
        // asserted positions and opacities are the settled values.
        for (var i = 0; i < 10; i++)
        {
            ui.Update();
            ui.Prepare();
        }

        // Mid-drag: the dragged tab leaves its slot (it becomes an invisible
        // phantom, hidden once the hole moves away) and the target tab slides
        // right by exactly the dragged tab's width to open the hole.
        var dragged = FindDockTab(ui.Content!, "HIERARCHY");
        var target = FindDockTab(ui.Content!, "VIEWPORT");
        Assert.NotNull(dragged);
        Assert.NotNull(target);
        Assert.Contains("dock-tab-dragging", dragged!.Classes);
        Assert.Equal(0f, dragged!.ComputedStyle.Opacity);
        var translate = Assert.Single(target!.ComputedStyle.Transform.Ops,
            op => op.Type == TransformOpType.TranslateX);
        Assert.True(translate.A > 0);

        // The layout box itself must not move: hit-testing and the drop
        // indicator rely on the pre-shift positions.
        Assert.Equal(viewportTab.Layout.X, target.Layout.X, precision: 1);

        // The shift opens a hole exactly as wide as the dragged tab (its width
        // plus the tab gap): the neighbour never slides onto the dragged tab's
        // slot and renders on top of it (the reported bug) — the dragged tab
        // is hidden, and the empty space where it will land has its size.
        Assert.Equal(hierarchyTab.Layout.Width + 4f, translate.A, precision: 1);

        ui.ProcessPointerUp(targetX, targetY);
        ui.Update();
        ui.Prepare();

        // After the drop the slide is gone: no dimmed tab, no transforms left.
        Assert.Empty(TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-tab-dragging")));
        foreach (var tab in TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-tab")))
            Assert.True(tab.ComputedStyle.Transform.IsNone || tab.ComputedStyle.Transform.IsIdentity);
    }

    [Fact]
    public void DraggingTabRightwardShiftsTheTabBeforeTheInsertionPointLeft()
    {
        // Two-tab group: VIEWPORT then HIERARCHY (docked in).
        using var ui = DockAsTab(EditorPageCompositionTests.CreateEditorUi(), "HIERARCHY", "VIEWPORT");
        var content = ui.Content!;
        var viewportTab = FindDockTab(content, "VIEWPORT");
        var hierarchyTab = FindDockTab(content, "HIERARCHY");
        Assert.NotNull(viewportTab);
        Assert.NotNull(hierarchyTab);

        // VIEWPORT (left tab) dragged to the right of HIERARCHY: the hole
        // slides right, so HIERARCHY must shift left (negative translate) out
        // of the way instead of staying put and overlapping.
        var grabX = viewportTab!.Layout.X + 5;
        var grabY = viewportTab.Layout.Y + 5;
        var targetX = hierarchyTab!.Layout.Right + 1;
        var targetY = hierarchyTab.Layout.Y + 5;

        ui.ProcessPointerDown(grabX, grabY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerMove(targetX, targetY);
        ui.Update();
        ui.Prepare();
        // Let the 140ms slide/fade transitions run to completion.
        for (var i = 0; i < 10; i++)
        {
            ui.Update();
            ui.Prepare();
        }

        var dragged = FindDockTab(ui.Content!, "VIEWPORT");
        var neighbour = FindDockTab(ui.Content!, "HIERARCHY");
        Assert.NotNull(dragged);
        Assert.NotNull(neighbour);
        Assert.Equal(0f, dragged!.ComputedStyle.Opacity);
        var translate = Assert.Single(neighbour!.ComputedStyle.Transform.Ops,
            op => op.Type == TransformOpType.TranslateX);
        Assert.True(translate.A < 0);

        // The neighbour slides left exactly onto the dragged tab's slot (its
        // visual left edge meets the phantom's left edge), never overlapping
        // it, while the layout box stays put for hit-testing.
        Assert.Equal(viewportTab.Layout.X, neighbour.Layout.X + translate.A, precision: 1);
        Assert.Equal(viewportTab.Layout.X, dragged.Layout.X, precision: 1);

        ui.ProcessPointerUp(targetX, targetY);
        ui.Update();
        ui.Prepare();

        // The drop reorders the pair: HIERARCHY first, then VIEWPORT.
        var group = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-group"))
            .Single(group => TestUi.Texts(group).Contains("VIEWPORT") && TestUi.Texts(group).Contains("HIERARCHY"));
        var tabLabels = TestUi.FindAll(group, p => p.Classes.Contains("dock-tab"))
            .Select(tab => Assert.Single(TestUi.Texts(tab)))
            .ToArray();
        Assert.Equal(new[] { "HIERARCHY", "VIEWPORT" }, tabLabels);
    }

    [Fact]
    public void SplitterTracksAbsolutePointerMovementAcrossFrames()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;
        var splitter = TestUi.FindAll(content, p => p.Classes.Contains("dock-splitter-v")).First();
        Assert.InRange(splitter.Layout.Width, 0f, 4f);
        var startX = splitter.Layout.X + splitter.Layout.Width / 2;
        var startY = splitter.Layout.Y + splitter.Layout.Height / 2;
        var initialX = splitter.Layout.X;

        var hit = ui.ProcessPointerDown(startX, startY);
        Assert.NotNull(hit);
        Assert.Contains("dock-splitter-v", hit!.Classes);
        ui.Update();
        ui.Prepare();

        // Deliver two separate movement events. The second event's delta is
        // absolute from the press, not relative to the previous event.
        ui.ProcessPointerMove(startX + 17, startY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerMove(startX + 34, startY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerUp(startX + 34, startY);
        ui.Update();
        ui.Prepare();

        var movedSplitter = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-splitter-v")).First();
        Assert.Equal(34f, movedSplitter.Layout.X - initialX, precision: 1);
    }

    [Fact]
    public void SplitterTracksVerticalPointerMovementAcrossFrames()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;
        var splitter = TestUi.FindAll(content, p => p.Classes.Contains("dock-splitter-h")).First();
        Assert.InRange(splitter.Layout.Height, 0f, 4f);
        var startX = splitter.Layout.X + splitter.Layout.Width / 2;
        var startY = splitter.Layout.Y + splitter.Layout.Height / 2;
        var initialY = splitter.Layout.Y;

        var hit = ui.ProcessPointerDown(startX, startY);
        Assert.NotNull(hit);
        Assert.Contains("dock-splitter-h", hit!.Classes);
        ui.Update();
        ui.Prepare();

        ui.ProcessPointerMove(startX, startY + 25);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerUp(startX, startY + 25);
        ui.Update();
        ui.Prepare();

        var movedSplitter = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-splitter-h")).First();
        Assert.Equal(25f, movedSplitter.Layout.Y - initialY, precision: 1);
    }

    [Fact]
    public void ClickingTabWithoutDraggingJustSwitchesThePane()
    {
        // Two-tab group: docking HIERARCHY into VIEWPORT leaves HIERARCHY
        // active. Clicking VIEWPORT without dragging must switch the active
        // pane back to the viewport without re-docking.
        using var ui = DockAsTab(EditorPageCompositionTests.CreateEditorUi(), "HIERARCHY", "VIEWPORT");
        var content = ui.Content!;
        var viewportTab = FindDockTab(content, "VIEWPORT");
        Assert.NotNull(viewportTab);
        Assert.Null(TestUi.FindAll(content, p => p.Classes.Contains("dock-pane-active"))
            .SingleOrDefault(pane => TestUi.Find(pane, p => p.Classes.Contains("viewport-toolbar")) is not null));

        ui.ProcessPointerDown(viewportTab!.Layout.X + 5, viewportTab.Layout.Y + 5);
        ui.ProcessPointerUp(viewportTab.Layout.X + 5, viewportTab.Layout.Y + 5);
        ui.Update();
        ui.Prepare();

        var activePanes = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-pane-active"));
        Assert.Contains(activePanes, pane => TestUi.Find(pane, p => p.Classes.Contains("viewport-toolbar")) is not null);
    }

    private static Panel? FindDockTab(Panel root, string title) =>
        TestUi.FindAll(root, p => p.Classes.Contains("dock-tab"))
            .FirstOrDefault(tab => TestUi.Texts(tab).Any(text => text.Contains(title, StringComparison.Ordinal)));
}
