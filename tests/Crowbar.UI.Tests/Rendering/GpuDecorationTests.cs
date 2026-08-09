using Crowbar.UI;
using Xunit;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// Verifies the GPU-decoration delegation: with <see cref="SkiaUiRenderer.GpuDecorations"/>
/// enabled, outer box-shadows and uniform solid borders are skipped by the
/// Skia raster and emitted as <see cref="SkiaUiRenderer.Decorations"/> regions,
/// and the delegation falls back to the CPU raster whenever compositing above
/// the flat UI texture would be wrong (later paint, ancestor clips, transforms,
/// filters, per-side border styles).
/// </summary>
public class GpuDecorationTests
{
    private static (byte R, byte G, byte B, byte A) Pixel(ReadOnlyMemory<byte> pixels, int width, int x, int y)
    {
        var span = pixels.Span;
        var offset = (y * width + x) * 4;
        return (span[offset], span[offset + 1], span[offset + 2], span[offset + 3]);
    }

    [Fact]
    public void GpuDisabledCollectsNoRegions()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; box-shadow: 0 10px 0 0 #ff0000; border: 2px solid #0000ff; }");
        ui.Render();
        Assert.Empty(ui.Renderer.Decorations);
    }

    [Fact]
    public void OuterShadowIsDelegatedAndSkippedBySkia()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        // A solid red band 10px below the box (y 60..70).
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; box-shadow: 0 10px 0 0 #ff0000; }");

        ui.Renderer.GpuDecorations = true;
        var pixels = ui.Render();
        var region = Assert.Single(ui.Renderer.Decorations);
        Assert.Equal(DecorationKind.OuterShadow, region.Kind);
        // The bitmap no longer contains the shadow: the band is transparent.
        Assert.Equal((0, 0, 0, 0), Pixel(pixels, 160, 50, 65));
        // The box interior is untouched.
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 30));

        // Turning the delegation off restores the CPU shadow.
        ui.Renderer.GpuDecorations = false;
        ui.Renderer.MarkDirty();
        pixels = ui.Render();
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 50, 65));
        Assert.Empty(ui.Renderer.Decorations);
    }

    [Fact]
    public void BlurredShadowRegionCoversBlurExtent()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; box-shadow: 0 10px 8px 0 #ff0000; }");

        ui.Renderer.GpuDecorations = true;
        ui.Render();
        var region = Assert.Single(ui.Renderer.Decorations);
        // The quad extends beyond the shape by the blur half-margin.
        Assert.True(region.QuadWidth > region.ShapeWidth);
        Assert.Equal(8f, region.BlurRadius);
        Assert.Equal(0f, region.SpreadRadius);
        // The shape sits 10px below the box top (box top 10 + offset 10), spread 0.
        Assert.Equal(20f, region.ShapeY, 2);
    }

    [Fact]
    public void UniformBorderIsDelegated()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; border: 2px solid #ff0000; }");

        ui.Renderer.GpuDecorations = true;
        var pixels = ui.Render();
        var region = Assert.Single(ui.Renderer.Decorations);
        Assert.Equal(DecorationKind.Border, region.Kind);
        Assert.Equal(2f, region.BorderWidth);
        // The bitmap shows the background under the border (the ring is GPU-side).
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 11, 30));
        // The interior is still the background.
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 30));

        // CPU fallback paints the border into the bitmap.
        ui.Renderer.GpuDecorations = false;
        ui.Renderer.MarkDirty();
        pixels = ui.Render();
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 11, 30));
    }

    [Fact]
    public void InsetShadowStaysOnCpu()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; box-shadow: inset 0 4px 0 0 #0000ff; }");

        ui.Renderer.GpuDecorations = true;
        var pixels = ui.Render();
        Assert.Empty(ui.Renderer.Decorations);
        Assert.Equal((0, 0, 255, 255), Pixel(pixels, 160, 50, 12));
    }

    [Fact]
    public void ShadowUnderLaterSiblingFallsBackToCpu()
    {
        using var ui = TestUi.Create(160, 160);
        var earlier = new Panel { TagName = "div" };
        earlier.AddClass("earlier");
        var later = new Panel { TagName = "div" };
        later.AddClass("later");
        ui.Screen.AddChild(earlier);
        ui.Screen.AddChild(later);
        // earlier's shadow band (y 60..70) is covered by later (y 55..95):
        // compositing the shadow above the flat texture would hide later.
        ui.LoadStyles("""
            .earlier { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px;
                       background-color: #00ff00; box-shadow: 0 20px 0 0 #ff0000; }
            .later   { position: absolute; left: 10px; top: 55px; width: 100px; height: 40px;
                       background-color: #0000ff; }
            """);

        ui.Renderer.GpuDecorations = true;
        var pixels = ui.Render();
        Assert.Empty(ui.Renderer.Decorations);
        // The later sibling's background covers the earlier shadow (CPU order).
        Assert.Equal((0, 0, 255, 255), Pixel(pixels, 160, 50, 65));
    }

    [Fact]
    public void ShadowUnderClippedAncestorFallsBackToCpu()
    {
        using var ui = TestUi.Create(160, 160);
        var container = new Panel { TagName = "div" };
        container.AddClass("container");
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        container.AddChild(box);
        ui.Screen.AddChild(container);
        // The container clips its content; the box's shadow escapes it, so the
        // GPU quad would cross the clip boundary.
        ui.LoadStyles("""
            .container { position: absolute; left: 10px; top: 10px; width: 120px; height: 60px; overflow: hidden; }
            .box       { position: absolute; left: 5px; top: 5px; width: 50px; height: 30px;
                         background-color: #00ff00; box-shadow: 0 40px 0 0 #ff0000; }
            """);

        ui.Renderer.GpuDecorations = true;
        ui.Render();
        Assert.Empty(ui.Renderer.Decorations);
    }

    [Fact]
    public void ScrolledChildShadowFallsBackToCpu()
    {
        using var ui = TestUi.Create(160, 160);
        var container = new Panel { TagName = "div" };
        container.AddClass("container");
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        container.AddChild(box);
        ui.Screen.AddChild(container);
        // The box's shadow is fully inside the container, but the container
        // scrolls its content: the paint position is shifted, so the GPU quad
        // (which is placed from the layout rect alone) would be misplaced.
        ui.LoadStyles("""
            .container { position: absolute; left: 10px; top: 10px; width: 120px; height: 60px; overflow: scroll; }
            .box       { position: absolute; left: 5px; top: 5px; width: 50px; height: 80px;
                         background-color: #00ff00; box-shadow: 0 4px 0 0 #ff0000; }
            """);

        ui.Renderer.GpuDecorations = true;
        ui.Render();
        // The box overflows the 60px container, so scrolling shifts its paint.
        Assert.True(container.MaxScrollY > 0);
        container.ScrollTo(0, container.MaxScrollY);
        ui.Render();
        Assert.Empty(ui.Renderer.Decorations);
    }

    [Fact]
    public void TransformedShadowFallsBackToCpu()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; box-shadow: 0 10px 0 0 #ff0000; transform: translate(20px, 0px); }");

        ui.Renderer.GpuDecorations = true;
        ui.Render();
        Assert.Empty(ui.Renderer.Decorations);
    }

    [Fact]
    public void PerSideBorderColorsFallBackToCpu()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; border: 2px solid #ff0000; border-bottom-color: #0000ff; }");

        ui.Renderer.GpuDecorations = true;
        ui.Render();
        Assert.Empty(ui.Renderer.Decorations);
    }
}
