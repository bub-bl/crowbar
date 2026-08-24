using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

// Shares the process-global EditorExplorerState with EditorPageCompositionTests
// and ExplorerRowHeightTests (both render the editor page through CreateEditorUi).
[Collection("EditorPage")]
public class ExplorerHierarchyTests
{
    [Fact]
    public void RowsNestUnderTheirParentAndHighlightTheViewportSelection()
    {
        var world = Guid.NewGuid();
        var level = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        var nodes = new List<EditorExplorerState.TreeNode>
        {
            new("World", world, null, "Solar/map/Bold/globe", IsFolder: true),
            new("Demo", level, world, string.Empty, IsFolder: true),
            new("Parent", parent, level, "Solar/ui/Bold/box-minimalistic", IsFolder: false),
            new("Child", child, parent, "Solar/ui/Bold/box-minimalistic", IsFolder: false)
        };
        using var ui = CreateEditorUiWith(nodes, selection: child);
        var rows = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("tree-row")).ToList();

        Assert.Equal(4, rows.Count);
        // Indentation grows with the depth: 12px per level plus a 6px base.
        Assert.Equal("6px", IndentOf(RowWithText(rows, "World")));
        Assert.Equal("18px", IndentOf(RowWithText(rows, "Demo")));
        Assert.Equal("30px", IndentOf(RowWithText(rows, "Parent")));
        Assert.Equal("42px", IndentOf(RowWithText(rows, "Child")));

        // The entity selected in the viewport is highlighted in the tree.
        var selected = RowWithText(rows, "Child");
        Assert.True(selected.Classes.Contains("tree-selected"));
        Assert.False(RowWithText(rows, "Parent").Classes.Contains("tree-selected"));
    }

    [Fact]
    public void ClickingARowRequestsSelectionInTheViewport()
    {
        var entity = Guid.NewGuid();
        var nodes = new List<EditorExplorerState.TreeNode>
        {
            new("World", Guid.NewGuid(), null, "Solar/map/Bold/globe", IsFolder: true),
            new("Cube", entity, null, "Solar/ui/Bold/box-minimalistic", IsFolder: false)
        };
        using var ui = CreateEditorUiWith(nodes, selection: null);
        var row = RowWithText(TestUi.FindAll(ui.Content!, p => p.Classes.Contains("tree-row")).ToList(), "Cube");

        ui.ProcessPointerDown(row.Layout.X + 4, row.Layout.Y + 4);
        ui.ProcessPointerUp(row.Layout.X + 4, row.Layout.Y + 4);

        // The host consumes the request and applies it to the viewport gizmos.
        Assert.Equal(entity, EditorExplorerState.ConsumeRequestedSelection());
    }

    [Fact]
    public void CollapsingAFolderHidesItsSubtree()
    {
        var world = Guid.NewGuid();
        var structures = Guid.NewGuid();
        var house = Guid.NewGuid();
        var nodes = new List<EditorExplorerState.TreeNode>
        {
            new("World", world, null, "Solar/map/Bold/globe", IsFolder: true),
            new("Structures", structures, world, string.Empty, IsFolder: true),
            new("House", house, structures, "Solar/ui/Bold/box-minimalistic", IsFolder: false)
        };
        using var ui = CreateEditorUiWith(nodes, selection: null);
        var rowsBefore = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("tree-row")).ToList();
        Assert.Equal(3, rowsBefore.Count);
        Assert.Equal("Solar/arrows/Bold/alt-arrow-down", CaretIconOf(RowWithText(rowsBefore, "Structures")));

        EditorExplorerState.ToggleCollapsed(structures);
        ui.Update();
        ui.Prepare();

        var rowsAfter = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("tree-row")).ToList();
        Assert.Equal(2, rowsAfter.Count); // the house is hidden under the collapsed folder
        Assert.DoesNotContain(rowsAfter, r => TestUi.Texts(r).Any(t => t == "House"));
        Assert.Equal("Solar/arrows/Bold/alt-arrow-right", CaretIconOf(RowWithText(rowsAfter, "Structures")));
    }

    private static UiSystem CreateEditorUiWith(IReadOnlyList<EditorExplorerState.TreeNode> nodes, Guid? selection)
    {
        var ui = EditorPageCompositionTests.CreateEditorUi();
        EditorExplorerState.Publish(nodes, selection);
        ui.Update();
        ui.Prepare();
        return ui;
    }

    private static Panel RowWithText(List<Panel> rows, string text) => rows.Single(r => TestUi.Texts(r).Any(t => t == text));

    private static string? IndentOf(Panel row) => row.InlineStyle.GetValueOrDefault("padding-left");

    /// <summary>The caret icon name of a folder row (text carets became arrow icons).</summary>
    private static string? CaretIconOf(Panel row) => TestUi.FindAll(row, p => p is Icon)
        .Select(icon => ((Icon)icon).Name)
        .FirstOrDefault(name => name is "Solar/arrows/Bold/alt-arrow-down" or "Solar/arrows/Bold/alt-arrow-right");
}
