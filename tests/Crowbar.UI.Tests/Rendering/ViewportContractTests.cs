using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// The DockArea publishes the docked 3D viewport's content rectangle to its
/// hosting <see cref="UiSystem.SceneViewport"/>; the engine host reads it to
/// confine the 3D scene to the viewport. These tests pin that contract to the
/// real editor layout. The contract is per-instance, so assertions are made
/// against the same UiSystem that laid out the panels (other editor tests
/// running in parallel cannot clobber it).
/// </summary>
public class ViewportContractTests
{
    [Fact]
    public void DockAreaPublishesTheViewportContentRect()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();

        var rect = ui.SceneViewport!.Value;

        // Inside the 1280x720 window, with a real (non-zero) size.
        Assert.True(rect.X >= 0);
        Assert.True(rect.Y >= 0);
        Assert.True(rect.Right <= 1280);
        Assert.True(rect.Bottom <= 720);
        Assert.True(rect.Width > 0);
        Assert.True(rect.Height > 0);

        // It matches the dock-content (the area below the tab bar) of the group
        // hosting the VIEWPORT tab, in absolute window coordinates.
        var dockContent = FindViewportDockContent(ui);
        Assert.Equal(dockContent.Layout.X, rect.X, precision: 1);
        Assert.Equal(dockContent.Layout.Y, rect.Y, precision: 1);
        Assert.Equal(dockContent.Layout.Width, rect.Width, precision: 1);
        Assert.Equal(dockContent.Layout.Height, rect.Height, precision: 1);
    }

    [Fact]
    public void ViewportIsNullBeforeTheFirstLayout()
    {
        var uiDir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "src", "Editor", "Ui"));
        using var ui = new UiSystem();
        ui.SetViewport(1280, 720);
        ui.RegisterRazorComponentsFromDirectory(uiDir);
        ui.Navigate("/editor");

        // No layout pass yet: the DockArea cannot know its geometry.
        Assert.Null(ui.SceneViewport);
    }

    [Fact]
    public void ViewportRectTracksDockResize()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var before = FindViewportDockContent(ui).Layout.Width;

        // The splitter between the explorer and the central viewport group.
        // Dragging it left widens the flexible viewport group.
        var splitter = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-splitter-v"))[0];
        var startX = splitter.Layout.X + splitter.Layout.Width / 2;
        var startY = splitter.Layout.Y + splitter.Layout.Height / 2;

        ui.ProcessPointerDown(startX, startY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerMove(startX - 60, startY);
        ui.Update();
        ui.Prepare();
        ui.ProcessPointerUp(startX - 60, startY);
        ui.Update();
        ui.Prepare();

        // The layout actually widened.
        var dockContent = FindViewportDockContent(ui);
        Assert.True(dockContent.Layout.Width > before, $"expected the viewport to widen beyond {before}, got {dockContent.Layout.Width}");

        // And the published contract follows the new layout.
        var published = ui.SceneViewport!.Value;
        Assert.Equal(dockContent.Layout.Width, published.Width, precision: 1);
        Assert.Equal(dockContent.Layout.Height, published.Height, precision: 1);
    }

    private static Panel FindViewportDockContent(UiSystem ui) =>
        TestUi.FindAll(ui.Content!, p => p.Classes.Contains("dock-content"))
            .Single(panel => panel.Parent!.Classes.Contains("dock-group") &&
                TestUi.Texts(panel.Parent).Any(text => text.Contains("VIEWPORT", StringComparison.Ordinal)));
}
