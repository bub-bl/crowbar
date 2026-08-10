using Crowbar.UI;
using Xunit;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// Guards the partial-raster cull: a panel whose own box misses the damage
/// must not be skipped when its children overflow it, otherwise paint-only
/// redraws erase the overflowing content. Reproduces the real filters page
/// where hovering a z-index card cleared the cards hanging below the .body
/// (whose own box ends at y=720 while the cards are laid out at y=724+).
/// </summary>
public class OverflowCullTests
{
    private static (byte R, byte G, byte B, byte A) Pixel(ReadOnlyMemory<byte> pixels, int width, int x, int y)
    {
        var span = pixels.Span;
        var offset = (y * width + x) * 4;
        return (span[offset], span[offset + 1], span[offset + 2], span[offset + 3]);
    }

    private static (byte, byte, byte, byte) At(ReadOnlyMemory<byte> pixels, int width, Panel p)
        => Pixel(pixels, width, (int)(p.Layout.X + p.Layout.Width / 2), (int)(p.Layout.Y + p.Layout.Height / 2));

    /// <summary>
    /// A fixed-height container with three absolutely-positioned cards hanging
    /// below its own box, mirroring the .body / .zstack / .z-card structure of
    /// the filters demo (the cards overflow the container, so a paint-only
    /// redraw that damages only the cards must still repaint them).
    /// </summary>
    private static UiSystem Setup(int width, int height, out Panel high, out Panel mid, out Panel low)
    {
        var ui = TestUi.Create(width, height);
        ui.Renderer.GpuFills = true;
        var body = new Panel { TagName = "div" };
        body.AddClass("body");
        var column = new Panel { TagName = "div" };
        column.AddClass("column");
        // Pushes the section (and the z-cards) below the .body's own box so the
        // cards overflow their container, like the filters demo's column.
        var spacer = new Panel { TagName = "div" };
        spacer.AddClass("spacer");
        var section = new Panel { TagName = "div" };
        section.AddClass("section");
        var zstack = new Panel { TagName = "div" };
        zstack.AddClass("zstack");
        high = new Panel { TagName = "div" };
        high.AddClass("z-card");
        high.AddClass("z-high");
        high.AddChild(new Panel { TagName = "text", Text = "z3 · first in DOM" });
        mid = new Panel { TagName = "div" };
        mid.AddClass("z-card");
        mid.AddClass("z-mid");
        mid.AddChild(new Panel { TagName = "text", Text = "z2 · second in DOM" });
        low = new Panel { TagName = "div" };
        low.AddClass("z-card");
        low.AddClass("z-low");
        low.AddChild(new Panel { TagName = "text", Text = "z1 · last in DOM" });
        zstack.AddChild(high);
        zstack.AddChild(mid);
        zstack.AddChild(low);
        section.AddChild(zstack);
        column.AddChild(spacer);
        column.AddChild(section);
        body.AddChild(column);
        ui.Screen.AddChild(body);
        ui.LoadStyles("""
            .body { width: 100%; height: 650px; flex-direction: row; align-items: flex-start; }
            .column { width: 330px; margin: 20px 0 0 32px; gap: 22px; }
            .spacer { width: 330px; height: 700px; }
            .section { width: 330px; }
            .zstack { width: 320px; height: 270px; }
            .z-card { position: absolute; width: 190px; height: 90px; border-radius: 10px; padding: 10px 0 0 12px; color: white; font-size: 12px; box-sizing: border-box; }
            .z-high { z-index: 3; left: 120px; top: 8px; background-color: #2ecc71; }
            .z-mid  { z-index: 2; left: 60px; top: 84px; background-color: #4c9df0; }
            .z-low  { z-index: 1; left: 0px; top: 160px; background-color: #b3541e; }
            """);
        return ui;
    }

    [Fact]
    public void PaintOnlyRedrawDoesNotEraseOverflowingCards()
    {
        // The .body box ends at y=720 while the cards are laid out at
        // y=724..966 (they overflow the container). Hovering the middle card
        // invalidates only the two visible cards; the partial redraw must not
        // cull the .body subtree.
        using var ui = Setup(1920, 1080, out var high, out var mid, out var low);
        var initial = ui.Render();
        var baseHigh = At(initial, 1920, high);
        var baseMid = At(initial, 1920, mid);
        Assert.True(baseHigh.Item4 > 0 && baseMid.Item4 > 0,
            $"cards not painted at baseline: high={baseHigh} mid={baseMid} layouts: high={high.Layout} mid={mid.Layout} low={low.Layout}; " +
            $"fills={string.Join(";", ui.Renderer.Fills.Select(f => $"{f.X},{f.Y},{f.Width}x{f.Height}"))}");

        // Hover the top card (ancestors get hovered, cards get re-cascaded) —
        // the first hover is a full redraw because the whole path was dirty.
        ui.ProcessPointerMove(high.Layout.X + 10, high.Layout.Y + 10);
        var afterFirst = ui.Render();
        Assert.Equal(baseHigh, At(afterFirst, 1920, high));
        Assert.Equal(baseMid, At(afterFirst, 1920, mid));

        // Hover the middle card: only the two cards are damaged now (the
        // common ancestors stay hovered), so the render is a partial redraw.
        ui.ProcessPointerMove(mid.Layout.X + 10, mid.Layout.Y + 10);
        var afterSecond = ui.Render();
        Assert.Equal(baseHigh, At(afterSecond, 1920, high));
        Assert.Equal(baseMid, At(afterSecond, 1920, mid));

        // And back to nothing hovered.
        ui.ProcessPointerMove(0, 0);
        var afterNone = ui.Render();
        Assert.Equal(baseHigh, At(afterNone, 1920, high));
        Assert.Equal(baseMid, At(afterNone, 1920, mid));
    }

    [Fact]
    public void RealFiltersPageKeepsCardsAcrossHovers()
    {
        // End-to-end against the shipped filters page at 1080p (the z-cards are
        // laid out below the .body's own box there, so partial redraws must not
        // erase them).
        var uiDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Editor", "Ui"));
        using var ui = new UiSystem();
        ui.Screen.SetViewport(1920, 1080);
        ui.Renderer.Resize(1920, 1080);
        ui.RegisterRazorComponentFromFile("FilterDemo", Path.Combine(uiDir, "FilterDemo.razor"), "FilterDemo");
        ui.Renderer.GpuDecorations = true;
        ui.Renderer.GpuFills = true;
        ui.Navigate("/filters");
        ui.Render();

        var cards = new List<Panel>();
        void Walk(Panel? p)
        {
            if (p is null) return;
            if (p.Classes.Contains("z-card")) cards.Add(p);
            foreach (var c in p.Children) Walk(c);
        }
        Walk(ui.Content);
        Assert.Equal(3, cards.Count);
        var high = cards.First(p => p.Classes.Contains("z-high"));
        var mid = cards.First(p => p.Classes.Contains("z-mid"));
        Assert.True(high.Layout.Bottom > 720, "z-cards must overflow the .body for this test to be meaningful");

        var initial = ui.Render();
        var baseHigh = At(initial, 1920, high);
        var baseMid = At(initial, 1920, mid);
        Assert.True(baseHigh.Item4 > 0 && baseMid.Item4 > 0,
            $"z-mid/z-high not painted at baseline: high={baseHigh} mid={baseMid}");

        // Hover the top card, then the middle one (the second hover is the
        // partial redraw that used to erase the overflowing cards).
        ui.ProcessPointerMove(high.Layout.X + 10, high.Layout.Y + 10);
        var afterHigh = ui.Render();
        Assert.Equal(baseHigh, At(afterHigh, 1920, high));
        Assert.Equal(baseMid, At(afterHigh, 1920, mid));

        ui.ProcessPointerMove(mid.Layout.X + 10, mid.Layout.Y + 10);
        var afterMid = ui.Render();
        Assert.Equal(baseHigh, At(afterMid, 1920, high));
        Assert.Equal(baseMid, At(afterMid, 1920, mid));
    }
}
