using Crowbar.UI;

namespace Crowbar.UI.Tests.Styling;

public class ShadowTests
{
    private static ComputedStyle Style(string declarations)
    {
        var panel = new Panel();
        panel.AddClass("box");
        return StyleSheet.Parse($".box {{ {declarations} }}").Compute(panel);
    }

    [Fact]
    public void BoxShadowParsesOffsetBlurSpreadAndColor()
    {
        var style = Style("box-shadow: 2px 4px 8px 1px rgba(0, 0, 0, 0.5);");
        var shadow = Assert.Single(style.BoxShadows);
        Assert.Equal(2f, shadow.OffsetX);
        Assert.Equal(4f, shadow.OffsetY);
        Assert.Equal(8f, shadow.BlurRadius);
        Assert.Equal(1f, shadow.SpreadRadius);
        Assert.False(shadow.Inset);
        Assert.Equal(new UiColor(0, 0, 0, 128), shadow.Color);
    }

    [Fact]
    public void BoxShadowSupportsInsetAndNegativeOffsets()
    {
        var style = Style("box-shadow: inset -3px -5px 0 0 #ff0000;");
        var shadow = Assert.Single(style.BoxShadows);
        Assert.True(shadow.Inset);
        Assert.Equal(-3f, shadow.OffsetX);
        Assert.Equal(-5f, shadow.OffsetY);
        Assert.Equal(0f, shadow.BlurRadius);
        Assert.Equal(new UiColor(255, 0, 0, 255), shadow.Color);
    }

    [Fact]
    public void BoxShadowDefaultsSpreadBlurAndColor()
    {
        var style = Style("box-shadow: 0 0 0 4px #00ff00;");
        var shadow = Assert.Single(style.BoxShadows);
        Assert.Equal(4f, shadow.SpreadRadius);
        Assert.Equal(0f, shadow.BlurRadius);

        // 2 lengths only: x, y; default color is black.
        style = Style("box-shadow: 1px 1px;");
        shadow = Assert.Single(style.BoxShadows);
        Assert.Equal(UiColor.Black, shadow.Color);
    }

    [Fact]
    public void BoxShadowSupportsMultipleShadowsWithCommaColors()
    {
        var style = Style("box-shadow: 0 2px 4px rgba(255, 0, 0, 0.5), inset 0 1px 0 #0000ff;");
        Assert.Equal(2, style.BoxShadows.Length);
        Assert.Equal(new UiColor(255, 0, 0, 128), style.BoxShadows[0].Color);
        Assert.False(style.BoxShadows[0].Inset);
        Assert.True(style.BoxShadows[1].Inset);
        Assert.Equal(new UiColor(0, 0, 255, 255), style.BoxShadows[1].Color);
    }

    [Fact]
    public void BoxShadowNoneIsEmpty()
    {
        Assert.Empty(Style("box-shadow: none;").BoxShadows);
    }

    [Fact]
    public void InvalidBoxShadowIsIgnored()
    {
        var style = Style("box-shadow: banana 3px;");
        Assert.Empty(style.BoxShadows);
    }

    [Fact]
    public void TextShadowParsesOffsetBlurAndColor()
    {
        var style = Style("text-shadow: 1px 2px 3px #123456;");
        var shadow = Assert.Single(style.TextShadows);
        Assert.Equal(1f, shadow.OffsetX);
        Assert.Equal(2f, shadow.OffsetY);
        Assert.Equal(3f, shadow.BlurRadius);
        Assert.Equal(new UiColor(0x12, 0x34, 0x56, 0xff), shadow.Color);
    }

    [Fact]
    public void TextShadowInheritsLikeColor()
    {
        using var ui = TestUi.Create(320, 200);
        var parent = new Panel { TagName = "div" };
        parent.AddClass("parent");
        var child = new Panel { TagName = "text", Text = "hi" };
        parent.AddChild(child);
        ui.Screen.AddChild(parent);
        ui.LoadStyles(".parent { text-shadow: 2px 2px 0 #ff0000; }");
        ui.Prepare();

        Assert.Single(child.ComputedStyle.TextShadows);
        Assert.Equal(new UiColor(255, 0, 0, 255), child.ComputedStyle.TextShadows[0].Color);
    }

    [Fact]
    public void ShadowListEqualByContent()
    {
        Assert.True(CssProperties.TryGet("box-shadow", out var property));
        var a = Style("box-shadow: 0 2px 4px #000000;");
        var b = Style("box-shadow: 0 2px 4px #000000;");
        Assert.True(property!.ValuesEqual(property.GetValue(a), property.GetValue(b)));
    }
}
