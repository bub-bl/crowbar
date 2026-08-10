using Crowbar.UI;

namespace Crowbar.UI.Tests.Razor;

public class ImageRazorTests
{
    [Fact]
    public void ImgTagWiresSrcToImagePanelSource()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="gallery">
              <img src="textures/logo.png" class="thumb" />
              <image src="icons/close.png" />
            </div>
            """, "ImageDemo");
        ui.Render();

        var images = TestUi.FindAll(ui.Screen, p => p is Image);
        Assert.Equal(2, images.Count);
        Assert.Equal("textures/logo.png", ((Image)images[0]).Source);
        Assert.Equal("icons/close.png", ((Image)images[1]).Source);
        // The attribute stays available for attribute selectors.
        Assert.Equal("textures/logo.png", images[0].Attributes["src"]);
    }

    [Fact]
    public void BackgroundImageCssAppliesToRazorRenderedPanel()
    {
        using var ui = TestUi.Create();
        var cache = new UiImageCache();
        ui.Renderer.ImageCache = cache;
        cache.Register("img://red", MakeImage());
        ui.LoadRazor("""
            <div class="hero"></div>
            """, "ImageDemo");
        ui.LoadScopedStyles("ImageDemo", ".hero { width: 40px; height: 20px; background-image: url(img://red); background-size: 40px 20px; background-repeat: no-repeat; }", "b-imagedemo");
        ui.Render();

        var hero = TestUi.Find(ui.Screen, p => p.Classes.Contains("hero"));
        Assert.NotNull(hero);
        Assert.Equal("img://red", hero.ComputedStyle.BackgroundImage);

        var pixels = ui.Render().ToArray();
        var i = (5 * 640 + 5) * 4;
        Assert.True(pixels[i + 3] > 200 && pixels[i] > 200, "background image should paint in the hero box");
    }

    [Fact]
    public void RazorImgAndBackgroundImageResolveFromContentRoot()
    {
        // Mirrors the editor demo: an <img src> and a scoped-CSS background-image
        // both resolve relative paths against the image cache's content root.
        using var ui = TestUi.Create();
        var cache = new UiImageCache();
        using var tmp = TestUi.TempDir("img-demo");
        var uiDir = Path.Combine(tmp.Path, "Ui", "images");
        Directory.CreateDirectory(uiDir);
        using (var bitmap = new SkiaSharp.SKBitmap(20, 20, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque))
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.DodgerBlue);
            using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(uiDir, "demo.png"), encoded.ToArray());
        }
        cache.ContentRoot = tmp.Path;
        ui.Renderer.ImageCache = cache;

        ui.LoadRazor("""
            <img class="hero" src="Ui/images/demo.png" />
            <div class="bg"></div>
            """, "ImageDemo");
        ui.LoadScopedStyles("ImageDemo", """
            .hero { width: 40px; height: 40px; }
            .bg { width: 40px; height: 40px; background-image: url(Ui/images/demo.png);
                  background-size: cover; background-repeat: no-repeat; }
            """, "b-imagedemo");
        ui.Render();

        var pixels = ui.Render().ToArray();
        static (byte R, byte G, byte B, byte A) Pixel(byte[] buffer, int x, int y)
        {
            var i = (y * 640 + x) * 4;
            return (buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]);
        }

        // The <img> (fill) and the background (cover) both paint DodgerBlue at
        // the center of their 40x40 boxes (hero at (0,0), bg at (0,40)).
        var imgPixel = Pixel(pixels, 20, 20);
        Assert.True(imgPixel.A > 200 && imgPixel.B > 200 && imgPixel.R < 80, $"img should paint the file image, got {imgPixel}");
        var bgPixel = Pixel(pixels, 20, 60);
        Assert.True(bgPixel.A > 200 && bgPixel.B > 200 && bgPixel.R < 80, $"background-image should paint the file image, got {bgPixel}");
    }

    private static SkiaSharp.SKImage MakeImage()
    {
        using var bitmap = new SkiaSharp.SKBitmap(1, 1, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.Red);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return SkiaSharp.SKImage.FromEncodedData(data);
    }
}
