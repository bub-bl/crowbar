using Crowbar.UI;

namespace Crowbar.UI.Tests.Styling;

/// <summary>
/// Guards the two per-repaint style optimizations. The scoped re-cascade
/// (ApplyStylesTracked) must re-compute only the subtrees a style change can
/// actually affect — the dirty panel's own subtree when the sheet has no
/// sibling combinators or :focus-within — and the masked change detection
/// (ComputedStyle.StylesEqual) must still catch every cascade write,
/// including compound shorthands that expand to several properties.
/// </summary>
public class StyleChangeScopingTests
{    private const int Width = 320;
    private const int Height = 200;


    [Fact]
    public void HoverOnOneRowReCascadesOnlyItsSubtree()
    {
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.LoadStyles(".row { height: 20px; } .row:hover { background-color: #3366ff; }");
        var container = new Panel { TagName = "div" };
        var rows = new List<Panel>(50);
        var labels = new List<Panel>(50);
        for (var i = 0; i < 50; i++)
        {
            var row = new Panel { TagName = "div" };
            row.AddClass("row");
            var label = new Panel { TagName = "div", Text = "row " + i };
            row.AddChild(label);
            container.AddChild(row);
            rows.Add(row);
            labels.Add(label);
        }

        ui.Screen.AddChild(container);
        ui.Prepare();

        var rowStyles = rows.Select(row => row.ComputedStyle).ToArray();
        var labelStyles = labels.Select(label => label.ComputedStyle).ToArray();

        rows[25].SetHovered(true);
        ui.Prepare();

        // The hovered row picks up the :hover rule...
        Assert.Equal(new UiColor(0x33, 0x66, 0xff, 255), rows[25].ComputedStyle.BackgroundColor);
        Assert.NotSame(rowStyles[25], rows[25].ComputedStyle);
        // ...and its subtree is re-cascaded (inheritance still flows).
        Assert.NotSame(labelStyles[25], labels[25].ComputedStyle);

        // Sibling rows are provably unaffected (no sibling/focus-within rules):
        // their cascade inputs are unchanged, so their computed styles must be
        // left completely untouched.
        for (var i = 0; i < 50; i++)
        {
            if (i == 25) continue;
            Assert.Same(rowStyles[i], rows[i].ComputedStyle);
            Assert.Same(labelStyles[i], labels[i].ComputedStyle);
        }
    }

    [Fact]
    public void SiblingCombinatorRuleFallsBackToParentSubtree()
    {
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.LoadStyles(".row { height: 20px; } .row + .row { margin-top: 4px; }");
        var container = new Panel { TagName = "div" };
        var rows = new List<Panel>(10);
        for (var i = 0; i < 10; i++)
        {
            var row = new Panel { TagName = "div" };
            row.AddClass("row");
            container.AddChild(row);
            rows.Add(row);
        }

        ui.Screen.AddChild(container);
        ui.Prepare();
        Assert.Equal(4f, rows[3].LayoutMargin.Top, 3);

        var rowStyles = rows.Select(row => row.ComputedStyle).ToArray();
        rows[5].SetHovered(true);
        ui.Prepare();

        // A sibling-combinator rule means a change on one row can restyle its
        // siblings, so the whole parent subtree must be re-cascaded.
        for (var i = 0; i < 10; i++)
            Assert.NotSame(rowStyles[i], rows[i].ComputedStyle);
    }

    [Fact]
    public void FocusWithinRuleFallsBackToParentSubtree()
    {
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.LoadStyles(".wrap { height: 20px; } .wrap:focus-within { background-color: #2266cc; }");
        var wrap = new Panel { TagName = "div" };
        wrap.AddClass("wrap");
        var input = new Panel { TagName = "div" };
        wrap.AddChild(input);
        ui.Screen.AddChild(wrap);
        ui.Prepare();

        var wrapStyle = wrap.ComputedStyle;
        input.SetFocused(true);
        ui.Prepare();

        // :focus-within is the one pseudo-class that matches ancestors, so a
        // focus change must still reach the parent even though the focus
        // change happened on a descendant.
        Assert.Equal(new UiColor(0x22, 0x66, 0xcc, 255), wrap.ComputedStyle.BackgroundColor);
        Assert.NotSame(wrapStyle, wrap.ComputedStyle);
    }

    [Fact]
    public void MaskedStylesEqualDetectsCascadeWrites()
    {
        // Two pure-default styles compare equal without walking the registry.
        var a = new ComputedStyle();
        var b = new ComputedStyle();
        Assert.True(a.StylesEqual(b));

        // A compound shorthand (margin -> 4 leaves) on one side is detected.
        StyleSheet.Apply(b, new Dictionary<string, string> { ["margin"] = "5px" });
        Assert.False(a.StylesEqual(b));
        Assert.False(b.StylesEqual(a));

        // The same write on both sides is equal again.
        StyleSheet.Apply(a, new Dictionary<string, string> { ["margin"] = "5px" });
        Assert.True(a.StylesEqual(b));

        // A leaf longhand differs even when the compound marked the same bit.
        StyleSheet.Apply(a, new Dictionary<string, string> { ["margin-left"] = "7px" });
        Assert.False(a.StylesEqual(b));
        Assert.False(b.StylesEqual(a));

        // Border expands to all four sides' width/style/color leaves.
        var c = new ComputedStyle();
        StyleSheet.Apply(c, new Dictionary<string, string> { ["border"] = "2px solid #ff0000" });
        Assert.False(c.StylesEqual(new ComputedStyle()));
        Assert.True(c.StylesEqual(c.Clone()));
    }

    [Fact]
    public void MaskedCompareStillDetectsLayoutAndInheritedChanges()
    {
        // The masked StylesEqual drives the resting-style replacement; a change
        // must still be detected after a run of stable passes.
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.LoadStyles(".box { width: 100px; margin: 10px; background-color: #0000ff; }");
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        ui.Screen.AddChild(box);
        ui.Prepare();
        Assert.Equal(10f, box.LayoutMargin.Left, 3);

        for (var i = 0; i < 3; i++)
        {
            ui.Screen.Invalidate();
            ui.Prepare();
        }

        // Compound margin change must reflow (resting style replaced).
        box.SetInlineStyle("margin", "30px");
        ui.Prepare();
        Assert.Equal(30f, box.LayoutMargin.Left, 3);

        // Paint-only change must not escape to layout.
        box.SetInlineStyle("background-color", "#ff0000");
        ui.Prepare();
        Assert.Equal(new UiColor(255, 0, 0, 255), box.ComputedStyle.BackgroundColor);
        Assert.Equal(100f, box.Layout.Width, 3);
    }
}
