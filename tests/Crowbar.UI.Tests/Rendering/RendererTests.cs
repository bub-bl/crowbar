using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

public class RendererTests
{
    [Fact]
    public void RasterizesToViewportSizedBuffer()
    {
        using var ui = TestUi.Create(width: 320, height: 200);
        var root = ui.Screen;
        var child = new Panel { TagName = "div" };
        child.AddClass("box");
        root.AddChild(child);
        ui.LoadStyles(".box { width: 100px; height: 40px; background-color: #ff0000; }");

        var pixels = ui.Render();
        Assert.Equal(320 * 200 * 4, pixels.Length);
    }

    [Fact]
    public void LayoutAndStyleRefreshOnInlineStyleChanges()
    {
        using var ui = TestUi.Create(width: 320, height: 200);
        var button = new Button("Click");
        button.SetInlineStyle("width", "80px");
        button.SetInlineStyle("height", "32px");
        ui.Screen.AddChild(button);
        ui.Render();

        button.SetInlineStyle("padding", "10px");
        button.SetInlineStyle("text-align", "center");
        button.SetInlineStyle("vertical-align", "center");
        button.SetInlineStyle("line-height", "32px");
        ui.Render();

        Assert.Equal(CssLength.Points(10), button.ComputedStyle.PaddingLeft);
        var buttonText = button.Children[0];
        Assert.Equal("center", buttonText.ComputedStyle.TextAlign);
        Assert.Equal("center", buttonText.ComputedStyle.VerticalAlign);
        Assert.Equal(32, buttonText.ComputedStyle.LineHeight);
    }

    [Fact]
    public void TransformedPanelPaintsChildrenAtLayoutPosition()
    {
        // Regression: a panel whose transform is set (here via a keyframe-style
        // identity translate) used to paint its children shifted by the panel's
        // own layout origin — the child Layout coordinates are global, but they
        // were used as offsets inside the transformed local space.
        using var ui = TestUi.Create(width: 400, height: 240);
        ui.LoadStyles("""
            .wrap { width: 140px; height: 40px; margin: 20px; background-color: #3478d4;
                    transform: translate(200px, 0px); color: #ffffff; }
            """);
        var wrap = new Panel { TagName = "div" };
        wrap.AddClass("wrap");
        wrap.AddChild(new Label("HELLO"));
        ui.Screen.AddChild(wrap);

        var pixels = ui.Render().ToArray();

        static int WhiteIn(byte[] buf, int width, int x0, int y0, int x1, int y1)
        {
            var white = 0;
            for (var y = y0; y < y1; y += 2)
            for (var x = x0; x < x1; x += 2)
            {
                var i = (y * width + x) * 4;
                if (buf[i + 3] > 200 && buf[i] > 200 && buf[i + 1] > 200) white++;
            }

            return white;
        }

        // The wrap is laid out at (20,20); translate(200px,0) moves the painted
        // box to (220,20), so the label glyphs must land around x 220-260. The
        // buggy code painted them at x 240+ / y 40+ (offset by the panel origin).
        var correct = WhiteIn(pixels, 400, 216, 20, 250, 40);
        Assert.True(correct > 4, "transformed panel child text is not painted at its layout position");
    }

    [Fact]
    public void NestedTransformedPanelsComposeMatrices()
    {
        // A transformed panel inside another transformed panel must chain its
        // matrix with the ancestor's, not replace it.
        using var ui = TestUi.Create(width: 400, height: 240);
        ui.LoadStyles("""
            .outer { width: 120px; height: 40px; margin: 20px; background-color: #3478d4;
                     transform: translate(60px, 0px); }
            .inner { width: 80px; height: 20px; margin: 10px; background-color: #245da8;
                     transform: translate(0px, 0px); color: #ffffff; }
            """);
        var outer = new Panel { TagName = "div" };
        outer.AddClass("outer");
        var inner = new Panel { TagName = "div" };
        inner.AddClass("inner");
        inner.AddChild(new Label("OK"));
        outer.AddChild(inner);
        ui.Screen.AddChild(outer);

        var pixels = ui.Render().ToArray();

        static int WhiteIn(byte[] buf, int width, int x0, int y0, int x1, int y1)
        {
            var white = 0;
            for (var y = y0; y < y1; y += 2)
            for (var x = x0; x < x1; x += 2)
            {
                var i = (y * width + x) * 4;
                if (buf[i + 3] > 200 && buf[i] > 200 && buf[i + 1] > 200) white++;
            }

            return white;
        }

        // outer at (20,20) + translate(60,0) → box at (80,20); inner at local
        // (10,10) → its text must land at (90,30)+. Without matrix composition
        // the inner drew at (10,10) in global space instead.
        var correct = WhiteIn(pixels, 400, 86, 28, 140, 50);
        Assert.True(correct > 4, "nested transformed panel text is not painted at the composed position");
    }

    [Fact]
    public void RendererReportsDirtyState()
    {
        using var renderer = new SkiaUiRenderer();
        renderer.Resize(100, 100);
        Assert.True(renderer.IsDirty);
        var screen = new ScreenPanel();
        renderer.Render(screen);
        Assert.False(renderer.IsDirty);
        renderer.MarkDirty();
        Assert.True(renderer.IsDirty);
    }
}
