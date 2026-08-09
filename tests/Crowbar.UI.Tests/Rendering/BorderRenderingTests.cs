using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

public class BorderRenderingTests
{
    private static (byte R, byte G, byte B, byte A) Pixel(ReadOnlyMemory<byte> pixels, int width, int x, int y)
    {
        var span = pixels.Span;
        var offset = (y * width + x) * 4;
        return (span[offset], span[offset + 1], span[offset + 2], span[offset + 3]);
    }

    [Fact]
    public void SolidBorderPaintsInsideTheBorderBox()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles("""
            .box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px;
                   background-color: #00ff00; border: 4px solid #ff0000; }
            """);

        var pixels = ui.Render();
        // Top border band: 4px red.
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 50, 12));
        // Left border band.
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 11, 30));
        // Interior: the green background, not the border.
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 40));
        // Just outside the box: transparent (no border bleed outside the box).
        Assert.Equal((0, 0, 0, 0), Pixel(pixels, 160, 50, 9));
    }

    [Fact]
    public void DashedBorderAlternatesDashAndGap()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        // Dash pattern 3*w on / 3*w off with w=4: dash 12px from x=10, gap 22-34, dash 58-70.
        ui.LoadStyles("""
            .box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px;
                   background-color: #00ff00; border: 4px dashed #ff0000; }
            """);

        var pixels = ui.Render();
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 63, 12)); // inside a dash
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 28, 12)); // inside a gap
    }

    [Fact]
    public void PerSideBorderStylesRenderIndependently()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles("""
            .box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px;
                   background-color: #00ff00;
                   border-top: 4px solid #ff0000; border-bottom: 4px solid #0000ff;
                   border-left-style: none; border-right-style: none; }
            """);

        var pixels = ui.Render();
        // Top edge: red border.
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 50, 12));
        // Bottom edge: blue border.
        Assert.Equal((0, 0, 255, 255), Pixel(pixels, 160, 50, 57));
        // Left edge has border-style none: the background shows through.
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 11, 30));
    }

    [Fact]
    public void OutlinePaintsOutsideTheBorderBoxWithoutAffectingLayout()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.LoadStyles("""
            .box { position: absolute; left: 20px; top: 20px; width: 100px; height: 50px;
                   background-color: #00ff00; outline: 3px solid #0000ff; }
            """);

        var pixels = ui.Render();
        Assert.Equal(100f, panel.Layout.Width);
        Assert.Equal(100f, panel.ClientWidth);
        // 1px above the border box: only the outline (3px wide, centered on the edge).
        Assert.Equal((0, 0, 255, 255), Pixel(pixels, 160, 60, 19));
        // The outline does not paint inside the box.
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 60, 30));
    }
}
