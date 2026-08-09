using Crowbar.UI;

namespace Crowbar.UI.Tests.Styling;

public class BorderOutlineTests
{
    private static ComputedStyle Style(string declarations)
    {
        var panel = new Panel();
        panel.AddClass("box");
        return StyleSheet.Parse($".box {{ {declarations} }}").Compute(panel);
    }

    [Fact]
    public void BorderShorthandSetsWidthStyleAndColorOnAllSides()
    {
        var style = Style("border: 2px solid #ff0000;");
        Assert.Equal(CssLength.Points(2), style.BorderTop);
        Assert.Equal(CssLength.Points(2), style.BorderRight);
        Assert.Equal(CssLength.Points(2), style.BorderBottom);
        Assert.Equal(CssLength.Points(2), style.BorderLeft);
        Assert.Equal("solid", style.BorderTopStyle);
        Assert.Equal("solid", style.BorderRightStyle);
        Assert.Equal("solid", style.BorderBottomStyle);
        Assert.Equal("solid", style.BorderLeftStyle);
        Assert.Equal(new UiColor(255, 0, 0, 255), style.BorderTopColor);
        Assert.Equal(new UiColor(255, 0, 0, 255), style.BorderLeftColor);
    }

    [Fact]
    public void BorderShorthandAcceptsPartialComponents()
    {
        // style only: the width/color keep their defaults (none -> solid is set).
        var style = Style("border: dashed;");
        Assert.Equal("dashed", style.BorderTopStyle);
        Assert.Equal(CssLength.Undefined, style.BorderTop);

        // width + style, no color.
        style = Style("border: 3px dotted;");
        Assert.Equal(CssLength.Points(3), style.BorderTop);
        Assert.Equal("dotted", style.BorderTopStyle);

        // color + style in any order.
        style = Style("border: #00ff00 solid;");
        Assert.Equal(new UiColor(0, 255, 0, 255), style.BorderTopColor);
        Assert.Equal("solid", style.BorderTopStyle);
    }

    [Fact]
    public void BorderStyleShorthandSupportsOneToFourValues()
    {
        var style = Style("border-style: dashed dotted;");
        Assert.Equal("dashed", style.BorderTopStyle);
        Assert.Equal("dotted", style.BorderRightStyle);
        Assert.Equal("dashed", style.BorderBottomStyle);
        Assert.Equal("dotted", style.BorderLeftStyle);

        style = Style("border-style: solid dashed dotted double;");
        Assert.Equal("solid", style.BorderTopStyle);
        Assert.Equal("dashed", style.BorderRightStyle);
        Assert.Equal("dotted", style.BorderBottomStyle);
        Assert.Equal("double", style.BorderLeftStyle);
    }

    [Fact]
    public void BorderColorShorthandSupportsOneToFourValues()
    {
        var style = Style("border-color: red blue green yellow;");
        Assert.Equal(new UiColor(255, 0, 0, 255), style.BorderTopColor);
        Assert.Equal(new UiColor(0, 0, 255, 255), style.BorderRightColor);
        Assert.Equal(new UiColor(0, 128, 0, 255), style.BorderBottomColor);
        Assert.Equal(new UiColor(255, 255, 0, 255), style.BorderLeftColor);
    }

    [Fact]
    public void BorderWidthShorthandAppliesToAllSides()
    {
        var style = Style("border-width: 1px 2px 3px 4px;");
        Assert.Equal(CssLength.Points(1), style.BorderTop);
        Assert.Equal(CssLength.Points(2), style.BorderRight);
        Assert.Equal(CssLength.Points(3), style.BorderBottom);
        Assert.Equal(CssLength.Points(4), style.BorderLeft);
    }

    [Fact]
    public void PerSideBorderLonghands()
    {
        var style = Style("""
            border-top-width: 5px;
            border-left-style: groove;
            border-bottom-color: #123456;
            """);
        Assert.Equal(CssLength.Points(5), style.BorderTop);
        Assert.Equal(CssLength.Undefined, style.BorderRight);
        Assert.Equal("groove", style.BorderLeftStyle);
        Assert.Equal("none", style.BorderRightStyle);
        Assert.Equal(new UiColor(0x12, 0x34, 0x56, 0xff), style.BorderBottomColor);
    }

    [Fact]
    public void OutlineShorthandSetsWidthStyleAndColor()
    {
        var style = Style("outline: 2px dashed #0000ff;");
        Assert.Equal(2f, style.OutlineWidth);
        Assert.Equal("dashed", style.OutlineStyle);
        Assert.Equal(new UiColor(0, 0, 255, 255), style.OutlineColor);
    }

    [Fact]
    public void OutlineWidthAcceptsCssKeywords()
    {
        Assert.Equal(1f, Style("outline: thin solid red;").OutlineWidth);
        Assert.Equal(3f, Style("outline: medium solid red;").OutlineWidth);
        Assert.Equal(5f, Style("outline: thick solid red;").OutlineWidth);
    }

    [Fact]
    public void OutlineOffsetCanBeNegative()
    {
        Assert.Equal(-3f, Style("outline-offset: -3px;").OutlineOffset);
        Assert.Equal(4f, Style("outline-offset: 4px;").OutlineOffset);
    }

    [Fact]
    public void BorderWidthParticipatesInLayout()
    {
        using var ui = TestUi.Create(320, 200);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { width: 100px; height: 40px; border: 4px solid #ff0000; }");
        ui.Render();

        Assert.Equal(100f, panel.Layout.Width);
        Assert.Equal(4f, panel.LayoutBorder.Left);
        Assert.Equal(92f, panel.ClientWidth);
        Assert.Equal(32f, panel.ClientHeight);
    }

    [Fact]
    public void OutlineDoesNotAffectLayout()
    {
        using var ui = TestUi.Create(320, 200);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { width: 100px; height: 40px; outline: 8px solid #ff0000; }");
        ui.Render();

        Assert.Equal(100f, panel.Layout.Width);
        Assert.Equal(100f, panel.ClientWidth);
    }

    [Fact]
    public void InvalidBorderValueIsIgnored()
    {
        var style = Style("border: definitely-not-css;");
        Assert.Equal(CssLength.Undefined, style.BorderTop);
        Assert.Equal("none", style.BorderTopStyle);

        style = Style("border-style: banana;");
        Assert.Equal("none", style.BorderTopStyle);
    }
}
