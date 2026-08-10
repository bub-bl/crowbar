using Crowbar.UI;

namespace Crowbar.UI.Tests.Styling;

public class ImageStyleTests
{
    private static ComputedStyle Compute(string css, Panel panel) => StyleSheet.Parse(css).Compute(panel);

    private static Panel PanelWithClass(string className)
    {
        var panel = new Panel { TagName = "div" };
        panel.AddClass(className);
        return panel;
    }

    [Fact]
    public void BackgroundImageParsesUrl()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { background-image: url(\"textures/wood.png\"); }", panel);
        Assert.Equal("textures/wood.png", style.BackgroundImage);
    }

    [Fact]
    public void BackgroundImageAcceptsSingleQuotedAndBareUrls()
    {
        var panel = PanelWithClass("box");
        Assert.Equal("a.png", Compute(".box { background-image: url('a.png'); }", panel).BackgroundImage);
        Assert.Equal("b.png", Compute(".box { background-image: url(b.png); }", panel).BackgroundImage);
    }

    [Fact]
    public void BackgroundImageNoneClears()
    {
        var panel = PanelWithClass("box");
        Assert.Null(Compute(".box { background-image: none; }", panel).BackgroundImage);
        Assert.Null(Compute(".box { background-image: url(x.png); background-image: none; }", panel).BackgroundImage);
    }

    [Fact]
    public void BackgroundShorthandRoutesTokens()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { background: url(img.png) no-repeat center / cover #123456; }", panel);
        Assert.Equal("img.png", style.BackgroundImage);
        Assert.Equal(new CssRepeat(RepeatMode.NoRepeat, RepeatMode.NoRepeat), style.BackgroundRepeat);
        Assert.Equal(BackgroundSizeType.Cover, style.BackgroundSize.Type);
        Assert.Equal(new UiColor(0x12, 0x34, 0x56, 255), style.BackgroundColor);
        Assert.Equal(CssLength.Percent(50), style.BackgroundPosition.X);
        Assert.Equal(CssLength.Percent(50), style.BackgroundPosition.Y);
    }

    [Fact]
    public void BackgroundShorthandWithoutSizeKeepsDefaults()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { background: #000000 url(img.png); }", panel);
        Assert.Equal("img.png", style.BackgroundImage);
        Assert.Equal(BackgroundSizeType.Auto, style.BackgroundSize.Type);
        Assert.Equal(new UiColor(0, 0, 0, 255), style.BackgroundColor);
    }

    [Fact]
    public void BackgroundSizeParsesKeywordsAndLengths()
    {
        var panel = PanelWithClass("box");
        Assert.Equal(BackgroundSizeType.Cover, Compute(".box { background-size: cover; }", panel).BackgroundSize.Type);
        Assert.Equal(BackgroundSizeType.Contain, Compute(".box { background-size: contain; }", panel).BackgroundSize.Type);

        var explicitSize = Compute(".box { background-size: 100px 50%; }", panel).BackgroundSize;
        Assert.Equal(BackgroundSizeType.Explicit, explicitSize.Type);
        Assert.Equal(CssLength.Points(100), explicitSize.Width);
        Assert.Equal(CssLength.Percent(50), explicitSize.Height);

        var single = Compute(".box { background-size: 50%; }", panel).BackgroundSize;
        Assert.Equal(CssLength.Percent(50), single.Width);
        Assert.Equal(CssLength.Auto, single.Height);

        var autoPair = Compute(".box { background-size: auto 40px; }", panel).BackgroundSize;
        Assert.Equal(CssLength.Auto, autoPair.Width);
        Assert.Equal(CssLength.Points(40), autoPair.Height);
    }

    [Fact]
    public void BackgroundPositionParsesKeywordsAndOffsets()
    {
        var panel = PanelWithClass("box");
        var topLeft = Compute(".box { background-position: top left; }", panel).BackgroundPosition;
        Assert.Equal(CssLength.Percent(0), topLeft.X);
        Assert.Equal(CssLength.Percent(0), topLeft.Y);

        var center = Compute(".box { background-position: center; }", panel).BackgroundPosition;
        Assert.Equal(CssLength.Percent(50), center.X);
        Assert.Equal(CssLength.Percent(50), center.Y);

        var rightBottom = Compute(".box { background-position: right bottom; }", panel).BackgroundPosition;
        Assert.Equal(CssLength.Percent(100), rightBottom.X);
        Assert.Equal(CssLength.Percent(100), rightBottom.Y);

        var offsets = Compute(".box { background-position: 10px 20px; }", panel).BackgroundPosition;
        Assert.Equal(CssLength.Points(10), offsets.X);
        Assert.Equal(CssLength.Points(20), offsets.Y);

        var singleOffset = Compute(".box { background-position: 12px; }", panel).BackgroundPosition;
        Assert.Equal(CssLength.Points(12), singleOffset.X);
        Assert.Equal(CssLength.Percent(50), singleOffset.Y);
    }

    [Fact]
    public void BackgroundRepeatParsesAllForms()
    {
        var panel = PanelWithClass("box");
        Assert.Equal(new CssRepeat(RepeatMode.Repeat, RepeatMode.Repeat),
            Compute(".box { background-repeat: repeat; }", panel).BackgroundRepeat);
        Assert.Equal(new CssRepeat(RepeatMode.NoRepeat, RepeatMode.NoRepeat),
            Compute(".box { background-repeat: no-repeat; }", panel).BackgroundRepeat);
        Assert.Equal(new CssRepeat(RepeatMode.Repeat, RepeatMode.NoRepeat),
            Compute(".box { background-repeat: repeat-x; }", panel).BackgroundRepeat);
        Assert.Equal(new CssRepeat(RepeatMode.NoRepeat, RepeatMode.Repeat),
            Compute(".box { background-repeat: repeat-y; }", panel).BackgroundRepeat);
        Assert.Equal(new CssRepeat(RepeatMode.Round, RepeatMode.Space),
            Compute(".box { background-repeat: round space; }", panel).BackgroundRepeat);
    }

    [Fact]
    public void ObjectFitAndObjectPositionParse()
    {
        var panel = PanelWithClass("box");
        Assert.Equal("cover", Compute(".box { object-fit: cover; }", panel).ObjectFit);
        Assert.Equal("scale-down", Compute(".box { object-fit: scale-down; }", panel).ObjectFit);
        Assert.Equal("none", Compute(".box { object-fit: none; }", panel).ObjectFit);
        // Unknown keywords are ignored, keeping the default (fill).
        Assert.Equal("fill", Compute(".box { object-fit: bogus; }", panel).ObjectFit);

        var position = Compute(".box { object-position: 25% 75%; }", panel).ObjectPosition;
        Assert.Equal(CssLength.Percent(25), position.X);
        Assert.Equal(CssLength.Percent(75), position.Y);
        // The default object-position is center.
        Assert.Equal(CssLength.Percent(50), new ComputedStyle().ObjectPosition.X);
        Assert.Equal(CssLength.Percent(50), new ComputedStyle().ObjectPosition.Y);
    }

    [Fact]
    public void AspectRatioSupportsAutoAndSlashSyntax()
    {
        var panel = PanelWithClass("box");
        var plain = Compute(".box { aspect-ratio: 1.5; }", panel);
        Assert.Equal(1.5f, plain.AspectRatio);
        Assert.False(plain.AspectRatioAuto);

        var slash = Compute(".box { aspect-ratio: 16 / 9; }", panel);
        Assert.Equal(16f / 9f, slash.AspectRatio, 3);

        var autoOnly = Compute(".box { aspect-ratio: auto; }", panel);
        Assert.Equal(0, autoOnly.AspectRatio);
        Assert.True(autoOnly.AspectRatioAuto);

        var autoWithFallback = Compute(".box { aspect-ratio: auto 4 / 3; }", panel);
        Assert.Equal(4f / 3f, autoWithFallback.AspectRatio, 3);
        Assert.True(autoWithFallback.AspectRatioAuto);
    }
}
