using Crowbar.UI;

namespace Crowbar.UI.Tests.Layout;

public class ImageLayoutTests
{
    private static UiImageCache CacheWith2x1() => Register2x1(new UiImageCache());

    private static UiImageCache Register2x1(UiImageCache cache)
    {
        cache.Register("img://2x1", 2, 1);
        return cache;
    }

    [Fact]
    public void ImagePanelSizesToIntrinsicDimensions()
    {
        var cache = CacheWith2x1();

        var root = new ScreenPanel();
        root.SetInlineStyle("align-items", "flex-start");
        var image = new Image { Source = "img://2x1" };
        root.AddChild(image);
        var engine = new YogaLayoutEngine();
        engine.Layout(root, 320, 200, null, cache);

        Assert.Equal(2, image.Layout.Width);
        Assert.Equal(1, image.Layout.Height);
    }

    [Fact]
    public void FixedWidthImageKeepsIntrinsicRatio()
    {
        var cache = CacheWith2x1();

        var root = new ScreenPanel();
        root.SetInlineStyle("align-items", "flex-start");
        var image = new Image { Source = "img://2x1" };
        image.SetInlineStyle("width", "100px");
        root.AddChild(image);
        var engine = new YogaLayoutEngine();
        engine.Layout(root, 320, 200, null, cache);

        Assert.Equal(100, image.Layout.Width);
        Assert.Equal(50, image.Layout.Height);
    }

    [Fact]
    public void AspectRatioAutoUsesImageIntrinsicRatio()
    {
        var cache = CacheWith2x1();

        var root = new ScreenPanel();
        root.SetInlineStyle("align-items", "flex-start");
        var image = new Image { Source = "img://2x1" };
        image.SetInlineStyle("width", "100px");
        image.SetInlineStyle("aspect-ratio", "auto");
        root.AddChild(image);
        var engine = new YogaLayoutEngine();
        engine.Layout(root, 320, 200, null, cache);

        Assert.Equal(100, image.Layout.Width);
        Assert.Equal(50, image.Layout.Height);
    }

    [Fact]
    public void ExplicitAspectRatioBeatsIntrinsicOnImage()
    {
        var cache = CacheWith2x1();

        var root = new ScreenPanel();
        root.SetInlineStyle("align-items", "flex-start");
        var image = new Image { Source = "img://2x1" };
        image.SetInlineStyle("width", "100px");
        image.SetInlineStyle("aspect-ratio", "4 / 1");
        root.AddChild(image);
        var engine = new YogaLayoutEngine();
        engine.Layout(root, 320, 200, null, cache);

        Assert.Equal(100, image.Layout.Width);
        Assert.Equal(25, image.Layout.Height);
    }
}
