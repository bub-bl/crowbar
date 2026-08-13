using Crowbar.UI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
        ui.Prepare();

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
        cache.Register("img://red", 40, 20);
        ui.LoadRazor("""
            <div class="hero"></div>
            """, "ImageDemo");
        ui.LoadScopedStyles("ImageDemo", ".hero { width: 40px; height: 20px; background-image: url(img://red); background-size: 40px 20px; background-repeat: no-repeat; }", "b-imagedemo");
        ui.Prepare();

        var hero = TestUi.Find(ui.Screen, p => p.Classes.Contains("hero"));
        Assert.NotNull(hero);
        Assert.Equal("img://red", hero.ComputedStyle.BackgroundImage);
        // The cache resolves the intrinsic size the layout engine needs.
        Assert.True(cache.TryGetSize("img://red", out var width, out var height));
        Assert.Equal(40, width);
        Assert.Equal(20, height);
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
        using (var image = new Image<Rgba32>(20, 20))
        {
            image.SaveAsPng(Path.Combine(uiDir, "demo.png"));
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
        ui.Prepare();

        // Both sources resolve the same file through the content root.
        var hero = (Image)TestUi.Find(ui.Screen, p => p is Image)!;
        Assert.Equal("Ui/images/demo.png", hero.Source);
        Assert.True(cache.TryGetSize(hero.Source, out var width, out var height));
        Assert.Equal(20, width);
        Assert.Equal(20, height);

        var bg = TestUi.Find(ui.Screen, p => p.Classes.Contains("bg"));
        Assert.NotNull(bg);
        Assert.Equal("Ui/images/demo.png", bg.ComputedStyle.BackgroundImage);
    }
}
