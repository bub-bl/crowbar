using Crowbar.UI;

namespace Crowbar.UI.Tests.Layout;

public class LayoutTests
{
    private static YogaLayoutEngine Layout(Panel root, float width, float height, string? css = null)
    {
        var engine = new YogaLayoutEngine();
        var sheet = css is null ? null : StyleSheet.Parse(css);
        engine.Layout(root, width, height, sheet);
        return engine;
    }

    [Fact]
    public void FixedSizedChildLaysOutAtOrigin()
    {
        var root = new ScreenPanel();
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        Layout(root, 320, 200, ".box { width: 100px; height: 40px; }");

        Assert.Equal(100, child.Layout.Width);
        Assert.Equal(40, child.Layout.Height);
    }

    [Fact]
    public void RowDirectionStacksChildrenHorizontally()
    {
        var root = new ScreenPanel();
        root.SetInlineStyle("flex-direction", "row");
        var a = new Panel { TagName = "div" };
        a.AddClass("a");
        var b = new Panel { TagName = "div" };
        b.AddClass("b");
        root.AddChild(a);
        root.AddChild(b);

        Layout(root, 320, 200, ".a { width: 50px; height: 20px; } .b { width: 30px; height: 20px; }");

        Assert.Equal(0, a.Layout.X);
        Assert.Equal(50, b.Layout.X);
    }

    [Fact]
    public void ColumnDirectionStacksChildrenVertically()
    {
        var root = new ScreenPanel { TagName = "screen" };
        var a = new Panel { TagName = "div" };
        var b = new Panel { TagName = "div" };
        root.AddChild(a);
        root.AddChild(b);

        Layout(root, 320, 200, "div { height: 40px; }");

        Assert.Equal(0, a.Layout.Y);
        Assert.Equal(40, b.Layout.Y);
    }

    [Fact]
    public void GapSeparatesRowChildren()
    {
        var root = new ScreenPanel { TagName = "screen" };
        root.SetInlineStyle("flex-direction", "row");
        root.SetInlineStyle("gap", "10px");
        var a = new Panel { TagName = "div" };
        var b = new Panel { TagName = "div" };
        root.AddChild(a);
        root.AddChild(b);

        Layout(root, 320, 200, "div { width: 40px; height: 20px; }");

        Assert.Equal(50, b.Layout.X);
    }

    [Fact]
    public void TextPanelGetsMeasuredSize()
    {
        var root = new ScreenPanel();
        var label = new Label("hello");
        root.AddChild(label);

        Layout(root, 320, 200);

        Assert.True(label.Layout.Width > 0);
        Assert.True(label.Layout.Height > 0);
    }

    [Fact]
    public void ContentBoxSizingAddsPaddingToWidth()
    {
        var root = new ScreenPanel();
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        Layout(root, 320, 200, ".box { width: 100px; padding: 10px; box-sizing: content-box; }");

        Assert.Equal(120, child.Layout.Width);
    }

    [Fact]
    public void BorderBoxSizingKeepsPaddingInsideWidth()
    {
        var root = new ScreenPanel();
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        Layout(root, 320, 200, ".box { width: 100px; padding: 10px; }");

        Assert.Equal(100, child.Layout.Width);
    }

    [Fact]
    public void TextPanelPaddingCountedOnce()
    {
        // Yoga adds the node padding around the measured text exactly once, so
        // the box grows by 20 (not 40 from a double count). Comparing the padded
        // and unpadded widths keeps the assertion independent of the exact font
        // metrics and of Yoga's pixel-grid rounding.
        var root = new ScreenPanel();
        root.SetInlineStyle("align-items", "flex-start");
        root.AddChild(new Label("hello"));
        Layout(root, 320, 200);
        var unpadded = root.Children[0].Layout.Width;

        var paddedRoot = new ScreenPanel();
        paddedRoot.SetInlineStyle("align-items", "flex-start");
        var padded = new Label("hello");
        padded.SetInlineStyle("padding", "10px");
        paddedRoot.AddChild(padded);
        Layout(paddedRoot, 320, 200);

        Assert.Equal(unpadded + 20, padded.Layout.Width, 0);
    }

    [Fact]
    public void LayoutPassesIncrementPerLayout()
    {
        var root = new ScreenPanel();
        root.AddChild(new Panel { TagName = "div" });
        var engine = Layout(root, 320, 200);
        Assert.Equal(1, engine.LayoutPasses);
        engine.Layout(root, 320, 200);
        Assert.Equal(2, engine.LayoutPasses);
    }

    [Fact]
    public void RootRuleAppliesInsideLayout()
    {
        var root = new ScreenPanel { TagName = "root" };
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        Layout(root, 320, 200, "root { width: 300px; } .box { width: 60px; height: 30px; }");

        Assert.Equal(CssLength.Points(300), root.ComputedStyle.Width);
        Assert.Equal(60, child.Layout.Width);
        Assert.Equal(30, child.Layout.Height);
    }

    [Fact]
    public void FlexWrapWrapsChildrenToNextLine()
    {
        var root = new ScreenPanel { TagName = "screen" };
        root.SetInlineStyle("flex-direction", "row");
        root.SetInlineStyle("flex-wrap", "wrap");
        var a = new Panel { TagName = "div" };
        var b = new Panel { TagName = "div" };
        var c = new Panel { TagName = "div" };
        root.AddChild(a);
        root.AddChild(b);
        root.AddChild(c);

        Layout(root, 200, 200, "div { width: 80px; height: 40px; }");

        Assert.Equal(0, a.Layout.Y);
        Assert.Equal(80, b.Layout.X);
        Assert.Equal(40, c.Layout.Y); // third item wrapped to the second line
    }

    [Fact]
    public void FlexShrinkShrinksChild()
    {
        var root = new ScreenPanel();
        root.SetInlineStyle("flex-direction", "row");
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        // 150px of content in a 100px row: flex-shrink must squeeze it to fit.
        Layout(root, 100, 100, ".box { width: 150px; height: 20px; flex-shrink: 1; }");

        Assert.Equal(100, child.Layout.Width);
    }

    [Fact]
    public void FlexBasisSetsInitialMainSize()
    {
        var root = new ScreenPanel();
        root.SetInlineStyle("flex-direction", "row");
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        Layout(root, 320, 100, ".box { flex-basis: 120px; height: 20px; }");

        Assert.Equal(120, child.Layout.Width);
    }

    [Fact]
    public void FlexShorthandGrowsChild()
    {
        var root = new ScreenPanel { TagName = "screen" };
        root.SetInlineStyle("flex-direction", "row");
        var a = new Panel { TagName = "div" };
        a.AddClass("a");
        var b = new Panel { TagName = "div" };
        b.AddClass("b");
        root.AddChild(a);
        root.AddChild(b);

        Layout(root, 300, 100, "div { height: 20px; } .a { flex: 1; } .b { flex: 2; }");

        Assert.Equal(100, a.Layout.Width);
        Assert.Equal(200, b.Layout.Width);
    }

    [Fact]
    public void PercentWidthResolvesAgainstParent()
    {
        var root = new ScreenPanel();
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        Layout(root, 320, 200, ".box { width: 50%; height: 20px; }");

        Assert.Equal(160, child.Layout.Width);
    }

    [Fact]
    public void PercentPaddingResolvesAgainstParent()
    {
        var root = new ScreenPanel();
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        Layout(root, 320, 200, ".box { width: 200px; height: 40px; padding: 10% 5%; }");

        // Percent padding resolves against the parent width (320): 10% vertical
        // = 32, 5% horizontal = 16. Yoga exposes the resolved values for the
        // renderer, kept in (top, right, bottom, left) order.
        Assert.Equal(new UiThickness(32, 16, 32, 16), child.LayoutPadding);
    }

    [Fact]
    public void ResolvedBoxEdgesKeepTheirOrdering()
    {
        var root = new ScreenPanel();
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        Layout(root, 320, 200, ".box { width: 100px; height: 40px; padding: 1px 2px 3px 4px; margin: 5px 6px 7px 8px; border: 9px solid #000000; }");

        // Each side must land on its own edge: a swapped mapping once pushed
        // text 12px down for asymmetric values like `padding: 0 12px`.
        Assert.Equal(new UiThickness(1, 2, 3, 4), child.LayoutPadding);
        Assert.Equal(new UiThickness(5, 6, 7, 8), child.LayoutMargin);
        Assert.Equal(new UiThickness(9, 9, 9, 9), child.LayoutBorder);
    }

    [Fact]
    public void AlignSelfOverridesAlignItems()
    {
        var root = new ScreenPanel();
        root.SetInlineStyle("align-items", "flex-start");
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        // align-items is flex-start, but align-self: stretch wins on this child
        // (stretch applies because the child has no explicit width).
        Layout(root, 320, 100, ".box { height: 20px; align-self: stretch; }");

        Assert.Equal(320, child.Layout.Width);
    }

    [Fact]
    public void AlignContentSpacesWrappedLines()
    {
        var root = new ScreenPanel { TagName = "screen" };
        root.SetInlineStyle("flex-direction", "row");
        root.SetInlineStyle("flex-wrap", "wrap");
        root.SetInlineStyle("align-content", "flex-end");
        var a = new Panel { TagName = "div" };
        var b = new Panel { TagName = "div" };
        var c = new Panel { TagName = "div" };
        root.AddChild(a);
        root.AddChild(b);
        root.AddChild(c);

        Layout(root, 200, 200, "div { width: 80px; height: 40px; }");

        // Two 40px lines packed to the bottom of the 200px container.
        Assert.Equal(160, c.Layout.Y);
    }

    [Fact]
    public void JustifyContentSpaceEvenly()
    {
        var root = new ScreenPanel { TagName = "screen" };
        root.SetInlineStyle("flex-direction", "row");
        root.SetInlineStyle("justify-content", "space-evenly");
        var a = new Panel { TagName = "div" };
        var b = new Panel { TagName = "div" };
        root.AddChild(a);
        root.AddChild(b);

        Layout(root, 300, 100, "div { width: 40px; height: 20px; }");

        // 220px free / 3 gaps = 73.3px before each item.
        Assert.Equal(73, a.Layout.X);
        Assert.Equal(187, b.Layout.X);
    }

    [Fact]
    public void AbsolutePositionOffsetsChild()
    {
        var root = new ScreenPanel();
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        Layout(root, 320, 200, ".box { position: absolute; top: 30px; left: 40px; width: 50px; height: 20px; }");

        Assert.Equal(40, child.Layout.X);
        Assert.Equal(30, child.Layout.Y);
    }

    [Fact]
    public void AbsolutePanelWithTopAndBottomAnchorsStretchesWithViewport()
    {
        // The editor page anchors its panels with top/bottom (instead of fixed
        // pixel heights) so the UI follows the window size. A panel with both
        // anchors and no height must grow when the viewport grows.
        var root = new ScreenPanel();
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);
        const string css = ".box { position: absolute; top: 44px; bottom: 264px; width: 100px; }";

        Layout(root, 1280, 720, css);
        Assert.Equal(44, child.Layout.Y);
        Assert.Equal(720 - 44 - 264, child.Layout.Height);

        // Same panel in a taller window: the height stretches with the viewport.
        Layout(root, 1280, 1080, css);
        Assert.Equal(44, child.Layout.Y);
        Assert.Equal(1080 - 44 - 264, child.Layout.Height);
    }

    [Fact]
    public void AbsolutePanelWithLeftAndRightAnchorsStretchesWithViewport()
    {
        // The viewport panel is anchored left/right between the side columns so
        // its width follows the window width.
        var root = new ScreenPanel();
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);
        const string css = ".box { position: absolute; left: 330px; right: 280px; top: 44px; bottom: 264px; }";

        Layout(root, 1280, 720, css);
        Assert.Equal(330, child.Layout.X);
        Assert.Equal(1280 - 330 - 280, child.Layout.Width);
        Assert.Equal(720 - 44 - 264, child.Layout.Height);

        Layout(root, 1600, 900, css);
        Assert.Equal(330, child.Layout.X);
        Assert.Equal(1600 - 330 - 280, child.Layout.Width);
        Assert.Equal(900 - 44 - 264, child.Layout.Height);
    }

    [Fact]
    public void AutoMarginCentersChild()
    {
        var root = new ScreenPanel();
        root.SetInlineStyle("flex-direction", "row");
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        Layout(root, 300, 100, ".box { width: 50px; height: 20px; margin: 0 auto; }");

        Assert.Equal(125, child.Layout.X);
    }

    [Fact]
    public void AspectRatioDerivesHeightFromWidth()
    {
        var root = new ScreenPanel();
        root.SetInlineStyle("align-items", "flex-start");
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        Layout(root, 320, 200, ".box { width: 100px; aspect-ratio: 2; }");

        Assert.Equal(50, child.Layout.Height);
    }

    [Fact]
    public void RtlDirectionStacksRowRightToLeft()
    {
        var root = new ScreenPanel { TagName = "screen" };
        root.SetInlineStyle("direction", "rtl");
        root.SetInlineStyle("flex-direction", "row");
        var a = new Panel { TagName = "div" };
        var b = new Panel { TagName = "div" };
        root.AddChild(a);
        root.AddChild(b);

        Layout(root, 320, 200, "div { width: 60px; height: 20px; }");

        // RTL: the first child sits at the right edge.
        Assert.Equal(260, a.Layout.X);
        Assert.Equal(200, b.Layout.X);
    }

    [Fact]
    public void ColumnReverseFlipsOrder()
    {
        var root = new ScreenPanel { TagName = "screen" };
        root.SetInlineStyle("flex-direction", "column-reverse");
        var a = new Panel { TagName = "div" };
        var b = new Panel { TagName = "div" };
        root.AddChild(a);
        root.AddChild(b);

        Layout(root, 320, 200, "div { height: 40px; }");

        // First child starts from the bottom edge in column-reverse.
        Assert.Equal(160, a.Layout.Y);
        Assert.Equal(120, b.Layout.Y);
    }

    [Fact]
    public void BorderReservesBoxSpace()
    {
        var root = new ScreenPanel();
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);

        Layout(root, 320, 200, ".box { width: 100px; height: 40px; border: 4px solid #000000; }");

        Assert.Equal(100, child.Layout.Width);
        Assert.Equal(new UiThickness(4, 4, 4, 4), child.LayoutBorder);
        Assert.Equal(new UiThickness(0, 0, 0, 0), child.LayoutPadding);
    }
}
