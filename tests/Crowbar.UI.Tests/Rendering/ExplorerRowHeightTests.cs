using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

// Shares the process-global EditorExplorerState with EditorPageCompositionTests
// (both render the editor page through CreateEditorUi).
[Collection("EditorPage")]

/// <summary>
/// Guards the vertical text-squashing fix. The editor's list rows (Explorer
/// tree, Inspector rows) are Razor components whose <em>root wrapper</em> is
/// the flex item of the scroll container. The glyphs are rasterized at full
/// font size, so if a row wrapper flex-shrinks below its line height when the
/// window gets shorter, the rows overlap and the text crushes onto itself.
/// </summary>
public class ExplorerRowHeightTests
{
    [Fact]
    public void TreeRowsKeepTheirHeightWhenTheWindowShrinksVertically()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        AssertRowsStackWithoutOverlap(ui, "tree-row", expectedGap: 1f);

        // Shrink the window so the dock area (and the Explorer's scroll area)
        // is far shorter than the ~24 rows × 20px of tree content.
        ui.SetViewport(1280, 300);
        ui.Update();
        Assert.True(ui.Prepare(), "the resized frame must trigger a repaint");

        AssertRowsStackWithoutOverlap(ui, "tree-row", expectedGap: 1f);
    }

    [Fact]
    public void InspectorRowsKeepTheirHeightWhenTheWindowShrinksVertically()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        AssertRowsStackWithoutOverlap(ui, "vec-row", expectedGap: 7f);

        ui.SetViewport(1280, 300);
        ui.Update();
        Assert.True(ui.Prepare(), "the resized frame must trigger a repaint");

        AssertRowsStackWithoutOverlap(ui, "vec-row", expectedGap: 7f);
    }

    /// <summary>
    /// The component root wrapper of each row is the flex item of the scroll
    /// container; it must keep its content height and the rows must stack
    /// edge-to-edge (height + gap) instead of overlapping.
    /// </summary>
    private static void AssertRowsStackWithoutOverlap(UiSystem ui, string className, float expectedGap)
    {
        var rows = TestUi.FindAll(ui.Content!, p => p.Classes.Contains(className));
        Assert.True(rows.Count >= 3, $"expected several {className} rows");

        var wrappers = rows.Select(row =>
        {
            Assert.NotNull(row.Parent);
            Assert.True(row.Parent!.Layout.Height > 0, $"{className} wrapper collapsed to zero");
            return row.Parent;
        }).ToList();

        var ordered = wrappers.OrderBy(wrapper => wrapper.Layout.Y).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var previousBottom = ordered[i - 1].Layout.Bottom;
            var nextTop = ordered[i].Layout.Y;
            // The rows may scroll, but must never overlap each other.
            Assert.True(nextTop >= previousBottom - 0.5f,
                $"{className} rows overlap: row {i} starts at {nextTop} before row {i - 1} ends at {previousBottom}");
        }

        // When everything fits (before the resize) the rows are exactly
        // height + gap apart, proving the wrappers track their content.
        var first = ordered[0];
        var second = ordered[1];
        Assert.Equal(first.Layout.Height + expectedGap, second.Layout.Y - first.Layout.Y, 1);
    }
}
