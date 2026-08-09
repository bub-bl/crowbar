using Crowbar.UI;
using Xunit;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// Verifies the GPU fill delegation: with <see cref="SkiaUiRenderer.GpuFills"/>
/// enabled, solid panel backgrounds are skipped by the Skia raster and emitted
/// as <see cref="SkiaUiRenderer.Fills"/> regions for the GPU compositor (drawn
/// below the UI texture), and the delegation falls back to the CPU raster
/// whenever a quad under the texture would be wrong (content painted before the
/// panel, ancestor clips, transforms, backdrop-filters).
/// </summary>
public class GpuFillTests
{
    private static (byte R, byte G, byte B, byte A) Pixel(ReadOnlyMemory<byte> pixels, int width, int x, int y)
    {
        var span = pixels.Span;
        var offset = (y * width + x) * 4;
        return (span[offset], span[offset + 1], span[offset + 2], span[offset + 3]);
    }

    [Fact]
    public void GpuDisabledCollectsNoFills()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; }");
        var pixels = ui.Render();
        Assert.Empty(ui.Renderer.Fills);
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 30));
    }

    [Fact]
    public void SolidFillIsDelegatedAndSkippedBySkia()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; }");

        ui.Renderer.GpuFills = true;
        var pixels = ui.Render();
        var region = Assert.Single(ui.Renderer.Fills);
        Assert.Equal(10f, region.X);
        Assert.Equal(10f, region.Y);
        Assert.Equal(100f, region.Width);
        Assert.Equal(50f, region.Height);
        Assert.Equal(new UiColor(0, 255, 0, 255), region.Color);
        // The bitmap no longer contains the fill: the interior is transparent.
        Assert.Equal((0, 0, 0, 0), Pixel(pixels, 160, 50, 30));

        // Turning the delegation off restores the CPU fill.
        ui.Renderer.GpuFills = false;
        ui.Renderer.MarkDirty();
        pixels = ui.Render();
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 30));
        Assert.Empty(ui.Renderer.Fills);
    }

    [Fact]
    public void OverlappingSiblingFillsChainOnGpu()
    {
        using var ui = TestUi.Create(160, 160);
        var a = new Panel { TagName = "div" };
        a.AddClass("a");
        var b = new Panel { TagName = "div" };
        b.AddClass("b");
        ui.Screen.AddChild(a);
        ui.Screen.AddChild(b);
        // b overlaps a and is painted after it: the GPU quads stack in paint
        // order under the texture, so both fills can be delegated.
        ui.LoadStyles("""
            .a { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #ff0000; }
            .b { position: absolute; left: 50px; top: 30px; width: 100px; height: 50px; background-color: #0000ff; }
            """);

        ui.Renderer.GpuFills = true;
        ui.Render();
        Assert.Equal(2, ui.Renderer.Fills.Count);
    }

    [Fact]
    public void TextPaintedBeforeBlocksFill()
    {
        using var ui = TestUi.Create(160, 160);
        var first = new Panel { TagName = "text", Text = "hello" };
        first.AddClass("first");
        var second = new Panel { TagName = "div" };
        second.AddClass("second");
        ui.Screen.AddChild(first);
        ui.Screen.AddChild(second);
        // second covers first's text: the text is in the texture, so a GPU
        // quad under it would let the text show through the fill.
        ui.LoadStyles("""
            .first  { position: absolute; left: 10px; top: 10px; width: 100px; height: 20px; color: #ffffff; }
            .second { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #0000ff; }
            """);

        ui.Renderer.GpuFills = true;
        var pixels = ui.Render();
        Assert.Empty(ui.Renderer.Fills);
        // second's fill stays on the CPU and covers first's text.
        Assert.Equal((0, 0, 255, 255), Pixel(pixels, 160, 50, 30));
    }

    [Fact]
    public void NonDelegatedEarlierFillBlocksLater()
    {
        using var ui = TestUi.Create(160, 160);
        var a = new Panel { TagName = "div" };
        a.AddClass("a");
        var b = new Panel { TagName = "div" };
        b.AddClass("b");
        ui.Screen.AddChild(a);
        ui.Screen.AddChild(b);
        // a is transformed, so its fill must stay on the CPU; b overlaps the
        // transformed extent and cannot be delegated over it either.
        ui.LoadStyles("""
            .a { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #ff0000; transform: scale(1.5); }
            .b { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #0000ff; }
            """);

        ui.Renderer.GpuFills = true;
        var pixels = ui.Render();
        Assert.Empty(ui.Renderer.Fills);
        // Both fills are CPU-painted, b over a.
        Assert.Equal((0, 0, 255, 255), Pixel(pixels, 160, 50, 30));
    }

    [Fact]
    public void ClippedChildFillFallsBackToCpu()
    {
        using var ui = TestUi.Create(160, 160);
        var container = new Panel { TagName = "div" };
        container.AddClass("container");
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        container.AddChild(box);
        ui.Screen.AddChild(container);
        // The box extends beyond the clipping container: its quad would escape
        // the clip, so the fill stays on the CPU.
        ui.LoadStyles("""
            .container { position: absolute; left: 10px; top: 10px; width: 120px; height: 60px; overflow: hidden; }
            .box       { position: absolute; left: 5px; top: 5px; width: 200px; height: 30px; background-color: #00ff00; }
            """);

        ui.Renderer.GpuFills = true;
        var pixels = ui.Render();
        Assert.Empty(ui.Renderer.Fills);
        // The fill is clipped to the container's padding box.
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 30));
        Assert.Equal((0, 0, 0, 0), Pixel(pixels, 160, 150, 30));
    }

    [Fact]
    public void BackdropFilterFillStaysOnCpu()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        // The backdrop compositor paints the panel's tint itself, so the fill
        // must not be delegated (it would double-tint the filtered backdrop).
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; backdrop-filter: blur(6px); }");

        ui.Renderer.GpuFills = true;
        var pixels = ui.Render();
        Assert.Empty(ui.Renderer.Fills);
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 30));
    }

    [Fact]
    public void TranslucentFillDelegatedWithBakedAlpha()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #ff000080; }");

        ui.Renderer.GpuFills = true;
        var pixels = ui.Render();
        var region = Assert.Single(ui.Renderer.Fills);
        Assert.Equal(new UiColor(255, 0, 0, 128), region.Color);
        Assert.Equal((0, 0, 0, 0), Pixel(pixels, 160, 50, 30));
    }

    [Fact]
    public void FillOnlyColorChangeSkipsTextureDamage()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; }");

        ui.Renderer.GpuFills = true;
        ui.Render();
        panel.SetInlineStyle("background-color", "#0000ff");
        var pixels = ui.Render();
        // The texture is untouched (the fill is GPU-side): no damage, and the
        // region carries the new color for the compositor to re-upload.
        Assert.Empty(ui.Renderer.DamageRects);
        var region = Assert.Single(ui.Renderer.Fills);
        Assert.Equal(new UiColor(0, 0, 255, 255), region.Color);
        Assert.Equal((0, 0, 0, 0), Pixel(pixels, 160, 50, 30));
    }

    [Fact]
    public void FillAndUniformBorderBothDelegated()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; border: 2px solid #ff0000; }");

        ui.Renderer.GpuDecorations = true;
        ui.Renderer.GpuFills = true;
        var pixels = ui.Render();
        Assert.Single(ui.Renderer.Fills);
        var region = Assert.Single(ui.Renderer.Decorations);
        Assert.Equal(DecorationKind.Border, region.Kind);
        // Neither the interior fill nor the border ring is in the bitmap.
        Assert.Equal((0, 0, 0, 0), Pixel(pixels, 160, 50, 30));
        Assert.Equal((0, 0, 0, 0), Pixel(pixels, 160, 11, 30));

        // CPU fallback restores both paints.
        ui.Renderer.GpuDecorations = false;
        ui.Renderer.GpuFills = false;
        ui.Renderer.MarkDirty();
        pixels = ui.Render();
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 30));
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 11, 30));
    }
}
