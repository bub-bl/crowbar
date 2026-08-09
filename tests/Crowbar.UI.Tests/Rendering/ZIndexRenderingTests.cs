using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

public class ZIndexRenderingTests
{
    private static (byte R, byte G, byte B, byte A) Pixel(ReadOnlyMemory<byte> pixels, int width, int x, int y)
    {
        var span = pixels.Span;
        var offset = (y * width + x) * 4;
        return (span[offset], span[offset + 1], span[offset + 2], span[offset + 3]);
    }

    [Fact]
    public void HigherZIndexPaintsAboveRegardlessOfDocumentOrder()
    {
        using var ui = TestUi.Create(160, 160);
        // Document order is high -> low, so without z-index the last child (the
        // lowest z-index panel) would paint on top.
        var high = new Panel { TagName = "div" };
        high.AddClass("high");
        var mid = new Panel { TagName = "div" };
        mid.AddClass("mid");
        var low = new Panel { TagName = "div" };
        low.AddClass("low");
        ui.Screen.AddChild(high);
        ui.Screen.AddChild(mid);
        ui.Screen.AddChild(low);
        ui.LoadStyles("""
            .high { position: absolute; left: 10px; top: 10px; width: 100px; height: 100px; background-color: #ff0000; z-index: 3; }
            .mid { position: absolute; left: 30px; top: 30px; width: 100px; height: 100px; background-color: #00ff00; z-index: 2; }
            .low { position: absolute; left: 50px; top: 50px; width: 100px; height: 100px; background-color: #0000ff; z-index: 1; }
            """);

        var pixels = ui.Render();
        // (60, 60) is inside all three panels: the red z:3 panel must be on top.
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 60, 60));
        // (40, 40) is inside high and mid only: red (z:3) above green (z:2).
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 40, 40));
        // (20, 20) is only inside the high panel.
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 20, 20));
    }

    [Fact]
    public void EqualZIndexKeepsDocumentOrder()
    {
        using var ui = TestUi.Create(160, 160);
        var first = new Panel { TagName = "div" };
        first.AddClass("first");
        var second = new Panel { TagName = "div" };
        second.AddClass("second");
        ui.Screen.AddChild(first);
        ui.Screen.AddChild(second);
        // Equal (non-zero) z-index: the stable sort must preserve document order,
        // so the second panel paints above the first.
        ui.LoadStyles("""
            .first { position: absolute; left: 10px; top: 10px; width: 100px; height: 60px; background-color: #ff0000; z-index: 1; }
            .second { position: absolute; left: 30px; top: 20px; width: 100px; height: 60px; background-color: #00ff00; z-index: 1; }
            """);

        var pixels = ui.Render();
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 60, 40));
        Assert.Equal((255, 0, 0, 255), Pixel(pixels, 160, 20, 30));
    }

    [Fact]
    public void NegativeZIndexPaintsBelowDefaultStacking()
    {
        using var ui = TestUi.Create(160, 160);
        var basePanel = new Panel { TagName = "div" };
        basePanel.AddClass("base");
        var behind = new Panel { TagName = "div" };
        behind.AddClass("behind");
        ui.Screen.AddChild(basePanel);
        ui.Screen.AddChild(behind);
        // The negative z-index panel is declared last but must paint below the
        // default (z-index 0) panel.
        ui.LoadStyles("""
            .base { position: absolute; left: 10px; top: 10px; width: 100px; height: 60px; background-color: #00ff00; }
            .behind { position: absolute; left: 30px; top: 20px; width: 100px; height: 60px; background-color: #ff0000; z-index: -1; }
            """);

        var pixels = ui.Render();
        Assert.Equal((0, 255, 0, 255), Pixel(pixels, 160, 60, 40));
    }

    [Fact]
    public void HitTestRespectsZIndex()
    {
        using var ui = TestUi.Create(160, 160);
        var high = new Panel { TagName = "div" };
        high.AddClass("high");
        var low = new Panel { TagName = "div" };
        low.AddClass("low");
        ui.Screen.AddChild(high);
        ui.Screen.AddChild(low);
        ui.LoadStyles("""
            .high { position: absolute; left: 10px; top: 10px; width: 100px; height: 100px; z-index: 3; }
            .low { position: absolute; left: 30px; top: 30px; width: 100px; height: 100px; z-index: 1; }
            """);
        ui.Render();

        // (60, 60) overlaps both panels; the higher z-index must win even though
        // the low panel is declared last in the tree.
        var hit = ui.Screen.HitTest(60, 60);
        Assert.Same(high, hit);

        // (20, 20) is only inside the high panel.
        Assert.Same(high, ui.Screen.HitTest(20, 20));
    }
}
