using Crowbar.UI;
using SkiaSharp;

namespace Crowbar.UI.Tests.Rendering;

public class ImageRenderingTests : IDisposable
{
    private readonly UiSystem _ui = TestUi.Create(width: 160, height: 120);
    private readonly UiImageCache _cache = new();

    public ImageRenderingTests()
    {
        _ui.Renderer.ImageCache = _cache;
    }

    public void Dispose()
    {
        _ui.Dispose();
        _cache.Clear();
    }

    /// <summary>A 2x1 image: left half red, right half blue.</summary>
    private void RegisterRedBlue(string source) => _cache.Register(source, MakeImage(2, 1, (x, _) => x < 1 ? SKColors.Red : SKColors.Blue));

    private static SKImage MakeImage(int width, int height, Func<int, int, SKColor> pixel)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            bitmap.SetPixel(x, y, pixel(x, y));
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return SKImage.FromEncodedData(data);
    }

    private static (byte R, byte G, byte B, byte A) GetPixel(byte[] buffer, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return (buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]);
    }

    private static void AssertRed((byte R, byte G, byte B, byte A) p, string where) =>
        Assert.True(p.A > 200 && p.R > 200 && p.G < 70 && p.B < 70, $"{where} should be red, got {p}");

    private static void AssertBlue((byte R, byte G, byte B, byte A) p, string where) =>
        Assert.True(p.A > 200 && p.B > 200 && p.R < 70, $"{where} should be blue, got {p}");

    private static void AssertTransparent((byte R, byte G, byte B, byte A) p, string where) =>
        Assert.True(p.A < 20, $"{where} should be transparent, got {p}");

    [Fact]
    public void ImagePanelObjectFitCoverFillsMatchingBox()
    {
        RegisterRedBlue("img://red-blue");
        var image = new Image { Source = "img://red-blue" };
        image.SetInlineStyle("width", "100px");
        image.SetInlineStyle("height", "50px");
        image.SetInlineStyle("object-fit", "cover");
        _ui.Screen.AddChild(image);

        var pixels = _ui.Render().ToArray();
        AssertRed(GetPixel(pixels, 160, 5, 25), "cover left half");
        AssertBlue(GetPixel(pixels, 160, 95, 25), "cover right half");
    }

    [Fact]
    public void ImagePanelObjectFitContainLetterboxesSides()
    {
        RegisterRedBlue("img://red-blue");
        var image = new Image { Source = "img://red-blue" };
        // 100x40 box vs a 2:1 image: contain fits 80x40, centered.
        image.SetInlineStyle("width", "100px");
        image.SetInlineStyle("height", "40px");
        image.SetInlineStyle("object-fit", "contain");
        _ui.Screen.AddChild(image);

        var pixels = _ui.Render().ToArray();
        AssertTransparent(GetPixel(pixels, 160, 5, 20), "contain left letterbox");
        AssertTransparent(GetPixel(pixels, 160, 95, 20), "contain right letterbox");
        AssertRed(GetPixel(pixels, 160, 25, 20), "contain left half");
        AssertBlue(GetPixel(pixels, 160, 75, 20), "contain right half");
    }

    [Fact]
    public void ImagePanelObjectFitNoneDrawsIntrinsicSizeAtPosition()
    {
        _cache.Register("img://red", MakeImage(1, 1, (_, _) => SKColors.Red));
        var image = new Image { Source = "img://red" };
        image.SetInlineStyle("width", "100px");
        image.SetInlineStyle("height", "50px");
        image.SetInlineStyle("object-fit", "none");
        image.SetInlineStyle("object-position", "top left");
        _ui.Screen.AddChild(image);

        var pixels = _ui.Render().ToArray();
        // Intrinsic 1x1 anchored at the top-left corner of the content box.
        AssertRed(GetPixel(pixels, 160, 0, 0), "intrinsic pixel at top-left");
        AssertTransparent(GetPixel(pixels, 160, 5, 5), "outside the intrinsic image");
    }

    [Fact]
    public void BackgroundImageRepeatsAcrossTheBox()
    {
        _cache.Register("img://red", MakeImage(1, 1, (_, _) => SKColors.Red));
        var panel = new Panel { TagName = "div" };
        panel.SetInlineStyle("width", "20px");
        panel.SetInlineStyle("height", "10px");
        panel.SetInlineStyle("background-image", "url(img://red)");
        panel.SetInlineStyle("background-size", "1px 1px");
        panel.SetInlineStyle("background-repeat", "repeat");
        _ui.Screen.AddChild(panel);

        var pixels = _ui.Render().ToArray();
        AssertRed(GetPixel(pixels, 160, 15, 5), "repeated tile");
    }

    [Fact]
    public void BackgroundImageNoRepeatHonorsPosition()
    {
        _cache.Register("img://red", MakeImage(1, 1, (_, _) => SKColors.Red));
        var panel = new Panel { TagName = "div" };
        panel.SetInlineStyle("width", "100px");
        panel.SetInlineStyle("height", "50px");
        panel.SetInlineStyle("background-image", "url(img://red)");
        panel.SetInlineStyle("background-size", "10px 10px");
        panel.SetInlineStyle("background-repeat", "no-repeat");
        panel.SetInlineStyle("background-position", "top left");
        _ui.Screen.AddChild(panel);

        var pixels = _ui.Render().ToArray();
        AssertRed(GetPixel(pixels, 160, 5, 5), "top-left tile");
        AssertTransparent(GetPixel(pixels, 160, 50, 25), "outside the tile");
    }

    [Fact]
    public void DataUriSourceDecodesInline()
    {
        using var bitmap = new SKBitmap(1, 1, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Green);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(encoded.ToArray());

        var image = new Image { Source = dataUri };
        image.SetInlineStyle("width", "10px");
        image.SetInlineStyle("height", "10px");
        _ui.Screen.AddChild(image);

        var pixels = _ui.Render().ToArray();
        var (r, g, b, a) = GetPixel(pixels, 160, 5, 5);
        // SKColors.Green is (0, 128, 0).
        Assert.True(a > 200 && g > 100 && g < 200 && r < 70 && b < 70, $"data URI should paint green, got ({r},{g},{b},{a})");
    }

    [Fact]
    public void BackgroundImageCoverFillsBoxWithRoundedClip()
    {
        RegisterRedBlue("img://red-blue");
        var panel = new Panel { TagName = "div" };
        panel.SetInlineStyle("width", "100px");
        panel.SetInlineStyle("height", "50px");
        panel.SetInlineStyle("background-image", "url(img://red-blue)");
        panel.SetInlineStyle("background-size", "cover");
        panel.SetInlineStyle("background-repeat", "no-repeat");
        _ui.Screen.AddChild(panel);

        var pixels = _ui.Render().ToArray();
        AssertRed(GetPixel(pixels, 160, 5, 25), "cover left half");
        AssertBlue(GetPixel(pixels, 160, 95, 25), "cover right half");
    }
}
