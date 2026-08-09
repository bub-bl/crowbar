using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

public class FilterRenderingTests
{
    private static (byte R, byte G, byte B, byte A) Pixel(ReadOnlyMemory<byte> pixels, int width, int x, int y)
    {
        var span = pixels.Span;
        var offset = (y * width + x) * 4;
        return (span[offset], span[offset + 1], span[offset + 2], span[offset + 3]);
    }

    [Fact]
    public void FilterTransformsTheElementsOwnPixels()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 40px; background-color: #ff0000; filter: brightness(0); }");

        var pixels = ui.Render();
        Assert.Equal((0, 0, 0, 255), Pixel(pixels, 160, 60, 30));
    }

    [Fact]
    public void GpuBackdropFilterLeavesTheBackdropToTheCompositor()
    {
        using var ui = TestUi.Create(160, 160);
        var basePanel = new Panel { TagName = "div" };
        basePanel.AddClass("base");
        var overlay = new Panel { TagName = "div" };
        overlay.AddClass("overlay");
        ui.Screen.AddChild(basePanel);
        ui.Screen.AddChild(overlay);
        ui.LoadStyles("""
            .base { position: absolute; left: 10px; top: 10px; width: 100px; height: 40px; background-color: #00ff00; }
            .overlay { position: absolute; left: 10px; top: 10px; width: 100px; height: 40px; backdrop-filter: brightness(0); }
            """);

        var pixels = ui.Render();
        // brightness(0) is GPU-expressible: Skia does NOT bake it, it records
        // the region for the GPU compositor instead. The transparent overlay
        // therefore leaves the pixels behind it untouched here.
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 60, 30));

        var region = Assert.Single(ui.Renderer.Backdrops);
        Assert.Equal(10, region.X);
        Assert.Equal(10, region.Y);
        Assert.Equal(100, region.Width);
        Assert.Equal(40, region.Height);
        Assert.Equal(0, region.Radius);
        Assert.Equal(1, region.Alpha);
        Assert.True(region.Filter.ToString().Contains("brightness", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TransparentPanelKeepsTheBackdropUntouched()
    {
        using var ui = TestUi.Create(160, 160);
        var basePanel = new Panel { TagName = "div" };
        basePanel.AddClass("base");
        var overlay = new Panel { TagName = "div" };
        overlay.AddClass("overlay");
        ui.Screen.AddChild(basePanel);
        ui.Screen.AddChild(overlay);
        ui.LoadStyles("""
            .base { position: absolute; left: 10px; top: 10px; width: 100px; height: 40px; background-color: #00ff00; }
            .overlay { position: absolute; left: 10px; top: 10px; width: 100px; height: 40px; }
            """);

        var pixels = ui.Render();
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 60, 30));
    }

    [Fact]
    public void FilterWithoutBackgroundStillFiltersChildren()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        var child = new Panel { TagName = "div" };
        child.AddClass("child");
        panel.AddChild(child);
        ui.Screen.AddChild(panel);
        ui.LoadStyles("""
            .box { position: absolute; left: 10px; top: 10px; width: 100px; height: 40px; filter: invert(1); }
            .child { position: absolute; left: 10px; top: 10px; width: 40px; height: 20px; background-color: #ffffff; }
            """);

        var pixels = ui.Render();
        // invert(1) turns the white child black.
        Assert.Equal((0, 0, 0, 255), Pixel(pixels, 160, 40, 25));
    }

    [Fact]
    public void BackdropFilterDoesNotClipOverflowingChildren()
    {
        using var ui = TestUi.Create(160, 160);
        var overlay = new Panel { TagName = "div" };
        overlay.AddClass("overlay");
        var child = new Panel { TagName = "div" };
        child.AddClass("child");
        overlay.AddChild(child);
        ui.Screen.AddChild(overlay);
        ui.LoadStyles("""
            .overlay { position: absolute; left: 10px; top: 10px; width: 100px; height: 40px; backdrop-filter: blur(2px); }
            .child { position: absolute; left: 90px; top: 10px; width: 40px; height: 20px; background-color: #ff0000; }
            """);

        var pixels = ui.Render();
        // The child spans x = 100..140, i.e. it sticks 30px past the overlay's
        // right edge (x = 110). backdrop-filter must not clip it.
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 120, 25));
    }

    [Fact]
    public void BackdropRegionCarriesTheCornerRadiusTintAndFilterChain()
    {
        using var ui = TestUi.Create(160, 160);
        var overlay = new Panel { TagName = "div" };
        overlay.AddClass("glass");
        ui.Screen.AddChild(overlay);
        ui.LoadStyles(".glass { position: absolute; left: 20px; top: 30px; width: 120px; height: 60px; border-radius: 8px; background-color: #ffffff12; backdrop-filter: blur(6px) saturate(1.5) brightness(1.15); }");

        ui.Render();
        var region = Assert.Single(ui.Renderer.Backdrops);
        Assert.Equal(20, region.X);
        Assert.Equal(30, region.Y);
        Assert.Equal(120, region.Width);
        Assert.Equal(60, region.Height);
        Assert.Equal(8, region.Radius);
        Assert.Equal(((byte)0xFF, (byte)0xFF, (byte)0xFF, (byte)0x12), (region.Tint.R, region.Tint.G, region.Tint.B, region.Tint.A));
        Assert.Equal("blur(6) saturate(1.5) brightness(1.15)", region.Filter.ToString());
    }

    [Fact]
    public void NonExpressibleBackdropChainFallsBackToTheCpuPath()
    {
        using var ui = TestUi.Create(160, 160);
        var basePanel = new Panel { TagName = "div" };
        basePanel.AddClass("base");
        var overlay = new Panel { TagName = "div" };
        overlay.AddClass("overlay");
        ui.Screen.AddChild(basePanel);
        ui.Screen.AddChild(overlay);
        // A blur that is not the first function cannot be evaluated by the
        // single-pass GPU compositor: Skia keeps the snapshot path and no GPU
        // region is recorded.
        ui.LoadStyles("""
            .base { position: absolute; left: 10px; top: 10px; width: 100px; height: 40px; background-color: #00ff00; }
            .overlay { position: absolute; left: 10px; top: 10px; width: 100px; height: 40px; backdrop-filter: brightness(0) blur(2px); }
            """);

        var pixels = ui.Render();
        // brightness(0) darkens the green base behind the transparent overlay.
        Assert.Equal((0, 0, 0, 255), Pixel(pixels, 160, 60, 30));
        Assert.Empty(ui.Renderer.Backdrops);
    }

    [Fact]
    public void ChainedFiltersApplyInOrder()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        // invert(1) then brightness(0): the red background becomes cyan, then black.
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 40px; background-color: #ff0000; filter: invert(1) brightness(0); }");

        var pixels = ui.Render();
        Assert.Equal((0, 0, 0, 255), Pixel(pixels, 160, 60, 30));
    }
}
