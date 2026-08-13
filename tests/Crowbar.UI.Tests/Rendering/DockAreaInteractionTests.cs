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
        ui.Render();

        Assert.NotNull(FindDockTab(ui.Content!, "EXPLORATEUR"));
        Assert.NotNull(FindDockTab(ui.Content!, "VIEWPORT"));
    }

    [Fact]
    public void DraggingTabToCenterOfAnotherGroupDocksItAsTab()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;

        // Grab the EXPLORATEUR tab and drag it onto the middle of the
        // VIEWPORT group (the center drop zone docks as a tab).
        var explorerTab = FindDockTab(content, "EXPLORATEUR");
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
        ui.Render();
        ui.ProcessPointerMove(targetX, targetY);
        ui.Update();
        ui.Render();
        ui.ProcessPointerUp(targetX, targetY);
        ui.Update();
        ui.Render();

        // EXPLORATEUR is now a tab of the same group as VIEWPORT, and the
        // emptied explorer group collapsed out of the layout.
        var tabs = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-tab"));
        var explorer = Assert.Single(tabs, tab => TestUi.Texts(tab).Any(text => text.Contains("EXPLORATEUR", StringComparison.Ordinal)));
        var viewport = Assert.Single(tabs, tab => TestUi.Texts(tab).Any(text => text.Contains("VIEWPORT", StringComparison.Ordinal)));
        Assert.Same(explorer.Parent, viewport.Parent);
        Assert.Single(TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-tab") && TestUi.Texts(p).Any(t => t.Contains("EXPLORATEUR", StringComparison.Ordinal))));
        Assert.Contains(TestUi.Texts(ui.Content!), text => text.Contains("Rechercher", StringComparison.Ordinal));
    }

    [Fact]
    public void DraggingTabToRightEdgeOfAnotherGroupSplitsIt()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;

        // Drag EXPLORATEUR onto the right edge of the VIEWPORT group: the
        // target group must be wrapped in a split with a fresh group holding
        // EXPLORATEUR on the right side.
        var explorerTab = FindDockTab(content, "EXPLORATEUR");
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
        ui.Render();
        ui.ProcessPointerMove(targetX, targetY);
        ui.Update();
        ui.Render();
        ui.ProcessPointerUp(targetX, targetY);
        ui.Update();
        ui.Render();

        // EXPLORATEUR sits in its own group now, next to (not inside) VIEWPORT.
        var explorerTabAfter = FindDockTab(ui.Content!, "EXPLORATEUR");
        Assert.NotNull(explorerTabAfter);
        Assert.NotSame(explorerTabAfter!.Parent, FindDockTab(ui.Content!, "VIEWPORT")?.Parent);
        var explorerPane = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-pane-active"))
            .Single(pane => TestUi.Texts(pane).Any(text => text.Contains("Rechercher", StringComparison.Ordinal)));
        Assert.True(explorerPane.Layout.Width > 0);
        Assert.True(explorerPane.Layout.Height > 0);
        Assert.Contains(TestUi.Texts(ui.Content!), text => text.Contains("Rechercher", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasingOutsideDockAreaCancelsDragWithoutLeavingGhost()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;
        var explorerTab = FindDockTab(content, "EXPLORATEUR");
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
        ui.Render();
        // (10, 10) is in the fixed top bar, outside DockArea. Pointer capture
        // must still receive the release and clear the transient drag state.
        ui.ProcessPointerUp(10, 10);
        ui.Update();
        ui.Render();

        var tabs = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-tab"));
        Assert.NotNull(FindDockTab(ui.Content!, "EXPLORATEUR"));
        Assert.NotNull(FindDockTab(ui.Content!, "VIEWPORT"));
        Assert.Equal(6, tabs.Count);
        Assert.Empty(TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-ghost")));
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
        ui.Render();

        Assert.Equal(before, ui.Renderer.LayoutPasses);
    }

    [Fact]
    public void DraggingTabBeforeAnotherTabReordersTheGroup()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;
        var contentTab = FindDockTab(content, "CONTENU");
        var worldTab = FindDockTab(content, "MONDE");
        Assert.NotNull(contentTab);
        Assert.NotNull(worldTab);

        var grabX = contentTab!.Layout.X + 5;
        var grabY = contentTab.Layout.Y + 5;
        var targetX = worldTab!.Layout.X + 1;
        var targetY = worldTab.Layout.Y + 5;

        ui.ProcessPointerDown(grabX, grabY);
        ui.Update();
        ui.Render();
        ui.ProcessPointerMove(targetX, targetY);
        ui.Update();
        ui.Render();
        ui.ProcessPointerUp(targetX, targetY);
        ui.Update();
        ui.Render();

        var bottomGroup = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-group"))
            .Single(group => TestUi.Texts(group).Contains("MONDE") && TestUi.Texts(group).Contains("CONTENU"));
        var tabLabels = TestUi.FindAll(bottomGroup, p => p.Classes.Contains("dock-tab"))
            .Select(tab => Assert.Single(TestUi.Texts(tab)))
            .ToArray();
        Assert.Equal(new[] { "CONTENU", "MONDE" }, tabLabels);
        Assert.Contains(TestUi.FindAll(bottomGroup, p => p.Classes.Contains("dock-tab-active")),
            tab => TestUi.Texts(tab).Contains("CONTENU"));
    }

    [Fact]
    public void DraggingTabSlidesNeighbouringTabsAsideAndSettlesOnRelease()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;
        var contentTab = FindDockTab(content, "CONTENU");
        var worldTab = FindDockTab(content, "MONDE");
        Assert.NotNull(contentTab);
        Assert.NotNull(worldTab);

        var grabX = contentTab!.Layout.X + 5;
        var grabY = contentTab.Layout.Y + 5;
        var targetX = worldTab!.Layout.X + 1;
        var targetY = worldTab.Layout.Y + 5;

        ui.ProcessPointerDown(grabX, grabY);
        ui.Update();
        ui.Render();
        ui.ProcessPointerMove(targetX, targetY);
        ui.Update();
        ui.Render();
        // Let the 140ms slide/fade transitions run to completion so the
        // asserted positions and opacities are the settled values.
        for (var i = 0; i < 10; i++)
        {
            ui.Update();
            ui.Render();
        }

        // Mid-drag: the dragged tab leaves its slot (it becomes an invisible
        // phantom, hidden once the hole moves away) and the target tab slides
        // right by exactly the dragged tab's width to open the hole.
        var dragged = FindDockTab(ui.Content!, "CONTENU");
        var target = FindDockTab(ui.Content!, "MONDE");
        Assert.NotNull(dragged);
        Assert.NotNull(target);
        Assert.Contains("dock-tab-dragging", dragged!.Classes);
        Assert.Equal(0f, dragged!.ComputedStyle.Opacity);
        var translate = Assert.Single(target!.ComputedStyle.Transform.Ops,
            op => op.Type == TransformOpType.TranslateX);
        Assert.True(translate.A > 0);

        // The layout box itself must not move: hit-testing and the drop
        // indicator rely on the pre-shift positions.
        Assert.Equal(worldTab.Layout.X, target.Layout.X, precision: 1);

        // The shift opens a hole exactly as wide as the dragged tab (its width
        // plus the tab gap): the neighbour never slides onto the dragged tab's
        // slot and renders on top of it (the reported bug) — the dragged tab
        // is hidden, and the empty space where it will land has its size.
        Assert.Equal(contentTab.Layout.Width + 4f, translate.A, precision: 1);

        ui.ProcessPointerUp(targetX, targetY);
        ui.Update();
        ui.Render();

        // After the drop the slide is gone: no dimmed tab, no transforms left.
        Assert.Empty(TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-tab-dragging")));
        foreach (var tab in TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-tab")))
            Assert.True(tab.ComputedStyle.Transform.IsNone || tab.ComputedStyle.Transform.IsIdentity);
    }

    [Fact]
    public void DraggingTabRightwardShiftsTheTabBeforeTheInsertionPointLeft()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;
        var worldTab = FindDockTab(content, "MONDE");
        var contentTab = FindDockTab(content, "CONTENU");
        Assert.NotNull(worldTab);
        Assert.NotNull(contentTab);

        // MONDE (left tab) dragged to the right of CONTENU: the hole slides
        // right, so CONTENU must shift left (negative translate) out of the
        // way instead of staying put and overlapping.
        var grabX = worldTab!.Layout.X + 5;
        var grabY = worldTab.Layout.Y + 5;
        var targetX = contentTab!.Layout.Right + 1;
        var targetY = contentTab.Layout.Y + 5;

        ui.ProcessPointerDown(grabX, grabY);
        ui.Update();
        ui.Render();
        ui.ProcessPointerMove(targetX, targetY);
        ui.Update();
        ui.Render();
        // Let the 140ms slide/fade transitions run to completion.
        for (var i = 0; i < 10; i++)
        {
            ui.Update();
            ui.Render();
        }

        var dragged = FindDockTab(ui.Content!, "MONDE");
        var neighbour = FindDockTab(ui.Content!, "CONTENU");
        Assert.NotNull(dragged);
        Assert.NotNull(neighbour);
        Assert.Equal(0f, dragged!.ComputedStyle.Opacity);
        var translate = Assert.Single(neighbour!.ComputedStyle.Transform.Ops,
            op => op.Type == TransformOpType.TranslateX);
        Assert.True(translate.A < 0);

        // The neighbour slides left exactly onto the dragged tab's slot (its
        // visual left edge meets the phantom's left edge), never overlapping
        // it, while the layout box stays put for hit-testing.
        Assert.Equal(worldTab.Layout.X, neighbour.Layout.X + translate.A, precision: 1);
        Assert.Equal(worldTab.Layout.X, dragged.Layout.X, precision: 1);

        ui.ProcessPointerUp(targetX, targetY);
        ui.Update();
        ui.Render();

        // The drop reorders the pair: CONTENU first, then MONDE.
        var bottomGroup = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-group"))
            .Single(group => TestUi.Texts(group).Contains("MONDE") && TestUi.Texts(group).Contains("CONTENU"));
        var tabLabels = TestUi.FindAll(bottomGroup, p => p.Classes.Contains("dock-tab"))
            .Select(tab => Assert.Single(TestUi.Texts(tab)))
            .ToArray();
        Assert.Equal(new[] { "CONTENU", "MONDE" }, tabLabels);
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
        ui.Render();

        // Deliver two separate movement events. The second event's delta is
        // absolute from the press, not relative to the previous event.
        ui.ProcessPointerMove(startX + 17, startY);
        ui.Update();
        ui.Render();
        ui.ProcessPointerMove(startX + 34, startY);
        ui.Update();
        ui.Render();
        ui.ProcessPointerUp(startX + 34, startY);
        ui.Update();
        ui.Render();

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
        ui.Render();

        ui.ProcessPointerMove(startX, startY + 25);
        ui.Update();
        ui.Render();
        ui.ProcessPointerUp(startX, startY + 25);
        ui.Update();
        ui.Render();

        var movedSplitter = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-splitter-h")).First();
        Assert.Equal(25f, movedSplitter.Layout.Y - initialY, precision: 1);
    }

    [Fact]
    public void ClickingTabWithoutDraggingJustSwitchesThePane()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;

        // The bottom group hosts MONDE (active) and CONTENU. Clicking CONTENU
        // without dragging must switch the active pane without re-docking.
        var contenuTab = FindDockTab(content, "CONTENU");
        Assert.NotNull(contenuTab);
        Assert.Null(TestUi.FindAll(content, p => p.Classes.Contains("dock-pane-active"))
            .SingleOrDefault(pane => TestUi.Texts(pane).Any(text => text.Contains("MODÈLES", StringComparison.Ordinal))));

        ui.ProcessPointerDown(contenuTab!.Layout.X + 5, contenuTab.Layout.Y + 5);
        ui.ProcessPointerUp(contenuTab.Layout.X + 5, contenuTab.Layout.Y + 5);
        ui.Update();
        ui.Render();

        var activePanes = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-pane-active"));
        Assert.Contains(activePanes, pane => TestUi.Texts(pane).Any(text => text.Contains("MODÈLES", StringComparison.Ordinal)));
    }

    private static Panel? FindDockTab(Panel root, string title) =>
        TestUi.FindAll(root, p => p.Classes.Contains("dock-tab"))
            .FirstOrDefault(tab => TestUi.Texts(tab).Any(text => text.Contains(title, StringComparison.Ordinal)));
}
