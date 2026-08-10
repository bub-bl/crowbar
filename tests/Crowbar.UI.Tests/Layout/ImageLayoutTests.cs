using Crowbar.UI;
using SkiaSharp;

namespace Crowbar.UI.Tests.Layout;

public class ImageLayoutTests
{
    private static SKImage MakeImage(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return SKImage.FromEncodedData(data);
    }

    [Fact]
    public void ImagePanelSizesToIntrinsicDimensions()
    {
        var cache = new UiImageCache();
        cache.Register("img://2x1", MakeImage(2, 1, SKColors.Red));

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
        var cache = new UiImageCache();
        cache.Register("img://2x1", MakeImage(2, 1, SKColors.Red));

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
        var cache = new UiImageCache();
        cache.Register("img://2x1", MakeImage(2, 1, SKColors.Red));

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
        var cache = new UiImageCache();
        cache.Register("img://2x1", MakeImage(2, 1, SKColors.Red));

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
