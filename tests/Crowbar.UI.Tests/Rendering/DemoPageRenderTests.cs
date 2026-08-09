using Crowbar.UI;
using SkiaSharp;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// Renders the real Editor demo page (Demo.razor + its compiled scoped CSS)
/// through the full pipeline — style computation, layout, animation driver,
/// Skia raster — and verifies, from the raw RGBA buffer, that the animated
/// elements' fills and text land exactly where the layout says they should.
/// This guards the transformed-panel paint path: a panel whose (keyframe)
/// animation sets a transform must still draw its children at their layout
/// position, not shifted by the panel's own offset.
/// </summary>
public class DemoPageRenderTests
{
    [Fact]
    public void DemoPageRendersAnimatedElementsTextInPlace()
    {
        var uiDir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "src", "Editor", "Ui"));
        Assert.True(Directory.Exists(uiDir), $"Ui directory not found: {uiDir}");

        using var ui = new UiSystem();
        ui.SetViewport(1280, 720);
        ui.RegisterRazorComponentsFromDirectory(uiDir);
        ui.Navigate("/");
        ui.Render();

        // Let the entrance animations (title-in, subtitle-skew) complete and the
        // infinite ones (title-glow, action-pulse, subtitle-drift) advance.
        for (var i = 0; i < 12; i++)
        {
            ui.Update(0.1f);
            ui.Render();
        }

        var pixels = ui.Render().ToArray();
        Assert.Equal(1280 * 720 * 4, pixels.Length);

        static (byte R, byte G, byte B, byte A) Pixel(byte[] buf, int x, int y)
        {
            var i = (y * 1280 + x) * 4;
            return (buf[i], buf[i + 1], buf[i + 2], buf[i + 3]);
        }

        // Count opaque light pixels (white or light-blue text) along a row.
        static int LightPixelsOnRow(byte[] buf, int x0, int x1, int y)
        {
            var light = 0;
            for (var x = x0; x < x1; x += 2)
            {
                var p = Pixel(buf, x, y);
                if (p.A > 200 && p.R > 140 && p.G > 140 && p.B > 140) light++;
            }

            return light;
        }

        // The title runs a transform + text-shadow keyframe animation; its text
        // must still be painted at the text panel's own layout rect.
        var title = TestUi.Find(ui.Content, p => p.Classes.Contains("title"));
        Assert.NotNull(title);
        var titleText = TestUi.Find(title, p => p.TagName == "text");
        Assert.NotNull(titleText);
        var titleRect = titleText!.Layout;
        Assert.True(LightPixelsOnRow(pixels, (int)titleRect.X, (int)(titleRect.X + titleRect.Width), (int)(titleRect.Y + titleRect.Height / 2)) > 4,
            "title text is not painted at its layout rect (transformed-panel offset bug)");

        // The subtitle carries two animations (skew + drift); its text is shifted
        // by a couple of pixels by the drift at sample time, so sum over rows.
        var subtitle = TestUi.Find(ui.Content, p => p.Classes.Contains("subtitle"));
        Assert.NotNull(subtitle);
        var subtitleText = TestUi.Find(subtitle, p => p.TagName == "text");
        Assert.NotNull(subtitleText);
        var subRect = subtitleText!.Layout;
        var subtitleLight = 0;
        for (var y = (int)subRect.Y + 1; y < subRect.Y + subRect.Height - 1; y += 2)
            subtitleLight += LightPixelsOnRow(pixels, (int)subRect.X, (int)(subRect.X + subRect.Width), y);
        Assert.True(subtitleLight > 8,
            "subtitle text is not painted at its layout rect");

        // The action buttons pulse via a transform animation: the fill must be an
        // opaque accent blue and the label must be painted inside the button.
        var action = TestUi.Find(ui.Content, p => p.Classes.Contains("action"));
        Assert.NotNull(action);
        var rect = action!.Layout;
        var center = Pixel(pixels, (int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
        Assert.True(center.A > 200, $"button fill alpha too low: {center}");
        Assert.True(center.B > 150 && center.R < 120, $"button fill not blue: {center}");
        var buttonWhite = 0;
        for (var y = (int)rect.Y + 2; y < rect.Y + rect.Height - 2; y += 2)
            buttonWhite += LightPixelsOnRow(pixels, (int)rect.X, (int)(rect.X + rect.Width), y);
        Assert.True(buttonWhite > 10,
            "action button label is not painted inside the button (transformed-panel offset bug)");
    }
}
