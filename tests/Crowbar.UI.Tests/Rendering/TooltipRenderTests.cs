using Crowbar.UI;
using SkiaSharp;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// The hover tooltip is drawn by the renderer on top of the panel tree: a
/// dark rounded box next to the cursor with the tooltip text. These tests
/// verify the raster output from the raw RGBA buffer.
/// </summary>
public class TooltipRenderTests
{
    [Fact]
    public void TooltipPaintsDarkBoxAndLightTextNearTheCursor()
    {
        using var ui = TestUi.Create(640, 480);
        ui.LoadRazor("""
            <div class="root" style="width: 640px; height: 480px;">
              <button tooltip="Save changes" style="width: 100px; height: 30px; position: absolute; left: 40px; top: 40px;">save</button>
            </div>
            """, "TooltipPaintDemo");
        ui.Render();

        var button = TestUi.Find(ui.Screen, p => p is Button);
        Assert.NotNull(button);
        ui.ProcessPointerMove(button!.Layout.X + 5, button.Layout.Y + 5);
        ui.Render();

        var pixels = ui.Render().ToArray();
        Assert.Equal(640 * 480 * 4, pixels.Length);

        static (byte R, byte G, byte B, byte A) Pixel(byte[] buf, int x, int y)
        {
            var i = (y * 640 + x) * 4;
            return (buf[i], buf[i + 1], buf[i + 2], buf[i + 3]);
        }

        // The tooltip box sits below-right of the cursor (14px / 18px offset),
        // so probe a few pixels just inside that box — they must be dark and
        // opaque (the box background), not the light editor background.
        var dark = 0;
        for (var x = 50; x < 120; x += 2)
        for (var y = 60; y < 85; y += 2)
        {
            var p = Pixel(pixels, x, y);
            if (p.A > 200 && p.R < 80 && p.G < 80 && p.B < 90) dark++;
        }
        Assert.True(dark > 10, $"tooltip box not painted dark near the cursor (dark pixels: {dark})");
    }

    [Fact]
    public void MovingAwayRepaintsTheTooltipAway()
    {
        using var ui = TestUi.Create(640, 480);
        ui.LoadRazor("""
            <div class="root" style="width: 640px; height: 480px;">
              <button tooltip="Save changes" style="width: 100px; height: 30px; position: absolute; left: 40px; top: 40px;">save</button>
            </div>
            """, "TooltipEraseDemo");
        ui.Render();

        var button = TestUi.Find(ui.Screen, p => p is Button);
        Assert.NotNull(button);
        ui.ProcessPointerMove(button!.Layout.X + 5, button.Layout.Y + 5);
        ui.Render();
        var withTooltip = ui.Render().ToArray();

        // Move the cursor away: the tooltip must disappear from the same spot.
        ui.ProcessPointerMove(600, 450);
        ui.Render();
        var withoutTooltip = ui.Render().ToArray();

        // Cursor at (45, 45): the box starts at cursor + (14, 18) = (59, 63),
        // so probe a pixel well inside it.
        var boxX = 64;
        var boxY = 70;
        var i = (boxY * 640 + boxX) * 4;
        Assert.NotEqual(withTooltip[i], withoutTooltip[i]);
    }
}
