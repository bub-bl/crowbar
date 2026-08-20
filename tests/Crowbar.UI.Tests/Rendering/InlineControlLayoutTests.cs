using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// Icon + text and text + caret controls must lay their children out on one
/// line (side by side), not stacked. The engine defaults a <c>div</c> to a
/// column flex box, so any control mixing an icon/prefix with its label has to
/// opt into <c>flex-direction: row</c> — when that is missing the icon ends up
/// drawn above the text instead of beside it. These tests pin that contract on
/// the real editor page.
/// </summary>
public class InlineControlLayoutTests
{
    private static void AssertSameLine(Panel left, Panel right, string context)
    {
        var leftCenterY = left.Layout.Y + left.Layout.Height / 2f;
        var rightCenterY = right.Layout.Y + right.Layout.Height / 2f;
        Assert.True(MathF.Abs(leftCenterY - rightCenterY) <= 1.5f,
            $"{context}: vertical centers differ ({leftCenterY} vs {rightCenterY}), so the children are stacked, not inline");
        Assert.True(left.Layout.X + left.Layout.Width <= right.Layout.X + 1f,
            $"{context}: first child should sit to the left of the second (left right edge {left.Layout.X + left.Layout.Width}, right left edge {right.Layout.X})");
    }

    [Fact]
    public void PlayButtonLaysIconBesideItsLabel()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;

        var play = TestUi.Find(content, p => p.Classes.Contains("tb-play"));
        Assert.NotNull(play);
        var icon = play!.Children.OfType<Icon>().Single();
        var label = play.Children.Single(c => c.TagName == "text");
        AssertSameLine(icon, label, "tb-play icon/label");
    }

    [Fact]
    public void ModeSelectorLaysIconLabelAndCaretOnOneLine()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;

        var mode = TestUi.Find(content, p => p.Classes.Contains("tb-mode"));
        Assert.NotNull(mode);
        var icons = mode!.Children.OfType<Icon>().ToList();
        Assert.Equal(2, icons.Count);
        var label = mode.Children.Single(c => c.TagName == "text");
        AssertSameLine(icons[0], label, "tb-mode icon/label");
        AssertSameLine(label, icons[1], "tb-mode label/caret");
    }

    [Fact]
    public void VecFieldLaysAxisPrefixBesideTheValue()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;

        // The vector editors are now editable per-axis inputs: the X/Y/Z prefix
        // is a sibling span sitting to the left of its input, all on one line.
        var row = TestUi.Find(content, p => p.Classes.Contains("vec-row"));
        Assert.NotNull(row);
        var axis = row!.Children.First(c => c.Classes.Contains("vec-axis"));
        var input = row.Children.First(c => c.Classes.Contains("vec-input"));
        AssertSameLine(axis, input, "vec-row axis/input");
    }

    [Fact]
    public void ViewportLocalToggleLaysLabelBesideTheCaret()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;

        var local = TestUi.Find(content, p => p.Classes.Contains("vt-local"));
        Assert.NotNull(local);
        var label = local!.Children.Single(c => c.TagName == "text");
        var caret = local.Children.Single(c => c.Classes.Contains("caret"));
        AssertSameLine(label, caret, "vt-local label/caret");
    }

    [Fact]
    public void InspectorSectionHeadLaysCaretAndTitleOnOneLine()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;

        // The collapsible inspector sections replaced the old fake
        // "MATERIALS Items: 5" title+count row. The same inline contract
        // now applies to the section head: caret and title sit side by side.
        var head = TestUi.Find(content, p => p.Classes.Contains("insp-section-head") &&
            TestUi.Texts(p).Any(t => t == "Transform"));
        Assert.NotNull(head);
        var caret = head!.Children.Single(c => c.Classes.Contains("caret"));
        var title = head.Children.Single(c => c.Classes.Contains("insp-section-title"));
        AssertSameLine(caret, title, "insp-section-head caret/title");
    }

}
