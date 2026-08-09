using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

public class ShadowRenderingTests
{
    private static (byte R, byte G, byte B, byte A) Pixel(ReadOnlyMemory<byte> pixels, int width, int x, int y)
    {
        var span = pixels.Span;
        var offset = (y * width + x) * 4;
        return (span[offset], span[offset + 1], span[offset + 2], span[offset + 3]);
    }

    [Fact]
    public void OuterShadowPaintsBelowTheBox()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        // No blur, offset 10px down: a solid red band from y 60 to 70.
        ui.LoadStyles("""
            .box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px;
                   background-color: #00ff00; box-shadow: 0 10px 0 0 #ff0000; }
            """);

        var pixels = ui.Render();
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 50, 65));  // inside the shadow band
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 30));  // box interior unaffected
    }

    [Fact]
    public void InsetShadowPaintsInsideTheTopEdge()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        // inset 0 4px 0 0 blue: a solid blue band along the top inside edge.
        ui.LoadStyles("""
            .box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px;
                   background-color: #00ff00; box-shadow: inset 0 4px 0 0 #0000ff; }
            """);

        var pixels = ui.Render();
        Assert.Equal((0, 0, 255, 255), Pixel(pixels, 160, 50, 12));  // top inset band
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 30));  // middle unaffected
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 57));  // bottom edge unaffected
    }

    [Fact]
    public void BlurredInsetShadowDoesNotMirrorToTheOppositeEdge()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        // Blurred inset shadow offset downward: the band must stay at the top.
        // The even-odd ring used to fill the shape's extension below the box,
        // so the blur painted a mirrored band along the bottom edge too.
        ui.LoadStyles("""
            .box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px;
                   background-color: #00ff00; box-shadow: inset 0 4px 10px 0 rgba(0, 0, 0, 0.8); }
            """);

        var pixels = ui.Render();
        Assert.NotEqual((0, 255, 0, 255), Pixel(pixels, 160, 50, 12));  // top band darkened
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 30));  // middle unaffected
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 57));  // bottom edge clean (no mirror)
    }

    [Fact]
    public void SpreadInflatesTheShadow()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        // spread 6px, no offset/blur: a red ring around the whole box.
        ui.LoadStyles("""
            .box { position: absolute; left: 20px; top: 20px; width: 60px; height: 40px;
                   background-color: #00ff00; box-shadow: 0 0 0 6px #ff0000; }
            """);

        var pixels = ui.Render();
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 50, 17));  // above the box (spread)
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 17, 40));  // left of the box
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 50, 40));  // box interior
    }

    [Fact]
    public void MultipleShadowsStackInOrder()
    {
        using var ui = TestUi.Create(160, 160);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        // Two zero-blur shadows with different x offsets: the red shadow
        // (offset 20) covers x 30-130, the blue (offset 40) x 50-150, both at
        // y 30-80. Below the box (y > 60) the overlap paints blue on top of
        // red (second shadow first), while x 35 shows only red.
        ui.LoadStyles("""
            .box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px;
                   background-color: #00ff00;
                   box-shadow: 20px 20px 0 0 #ff0000, 40px 20px 0 0 #0000ff; }
            """);

        var pixels = ui.Render();
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 35, 65));   // only red
        Assert.Equal((0, 0, 255, 255), Pixel(pixels, 160, 65, 65));   // overlap: blue on top
        Assert.Equal((0, 0, 255, 255), Pixel(pixels, 160, 140, 65));  // only blue
    }
}
