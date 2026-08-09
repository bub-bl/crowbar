using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// Guards the incremental (partial) raster path: paint-only invalidations must
/// repaint just the damaged regions without re-running Yoga, style changes
/// escalate to a full layout only when a layout-affecting property moved, and
/// the damaged rects are exposed for the GPU compositor's partial texture
/// uploads. Also covers the two regressions that made animated text invisible:
/// stale inherited opacity in children of an animating ancestor, and children
/// of an identity-transform panel being culled against screen-space damage.
/// </summary>
public class IncrementalRenderTests
{
    private const int Width = 320;
    private const int Height = 120;

    [Fact]
    public void PaintOnlyInvalidationSkipsLayoutAndReportsDamage()
    {
        using var ui = TestUi.Create(width: Width, height: Height);
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        box.AddChild(new Label("HI"));
        ui.Screen.AddChild(box);
        ui.LoadStyles(".box { width: 100px; height: 40px; background-color: #ff0000; color: #ffffff; }");
        ui.Render();
        var passes = ui.Renderer.LayoutPasses;

        // Caret blink / hover / scroll all funnel into InvalidatePaint: no
        // layout pass, and the damaged panel rect is reported.
        box.InvalidatePaint();
        ui.Render();

        Assert.Equal(passes, ui.Renderer.LayoutPasses);
        Assert.NotEmpty(ui.Renderer.DamageRects);
        Assert.Contains(ui.Renderer.DamageRects, d => d.X <= box.Layout.X && d.Y <= box.Layout.Y &&
            d.X + d.Width >= box.Layout.Right && d.Y + d.Height >= box.Layout.Bottom);
    }

    [Fact]
    public void NoOpRenderClearsDamage()
    {
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.Screen.AddChild(new Panel { TagName = "div" });
        ui.Render();

        ui.Render();

        Assert.Empty(ui.Renderer.DamageRects);
    }

    [Fact]
    public void LayoutAffectingStyleChangeEscalatesToLayout()
    {
        using var ui = TestUi.Create(width: Width, height: Height);
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        ui.Screen.AddChild(box);
        ui.LoadStyles(".box { width: 100px; height: 40px; background-color: #ff0000; }");
        ui.Render();
        var passes = ui.Renderer.LayoutPasses;

        box.SetInlineStyle("width", "140px");
        ui.Render();

        Assert.True(ui.Renderer.LayoutPasses > passes, "a width change must reflow the layout");
    }

    [Fact]
    public void PaintOnlyStyleChangeDoesNotReflow()
    {
        using var ui = TestUi.Create(width: Width, height: Height);
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        ui.Screen.AddChild(box);
        ui.LoadStyles(".box { width: 100px; height: 40px; background-color: #ff0000; }");
        ui.Render();
        var passes = ui.Renderer.LayoutPasses;

        box.SetInlineStyle("background-color", "#00ff00");
        ui.Render();

        Assert.Equal(passes, ui.Renderer.LayoutPasses);
    }

    [Fact]
    public void SetViewportSameSizeDoesNotInvalidate()
    {
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.Screen.AddChild(new Panel { TagName = "div" });
        ui.Render();
        ui.Render();

        // SetViewport runs on every render pass; it must be a no-op when the
        // size did not change, otherwise every frame would re-invalidate.
        ui.Screen.SetViewport(Width, Height);
        Assert.False(ui.Screen.LayoutDirty);

        ui.Render();
        Assert.Empty(ui.Renderer.DamageRects);
    }

    [Fact]
    public void AnimatedParentOpacityReachesTextChild()
    {
        // Regression: the cascade bakes inherited values (opacity, ...) into
        // children. When an ancestor's keyframe animation moves one, the
        // children kept the stale frame-0 value (0), so the text never became
        // visible — the old full-cascade-every-frame pipeline masked this.
        Keyframes.Define("cb-opacity-fade",
            KeyframeFrame.At(0, ("opacity", "0")),
            KeyframeFrame.At(1, ("opacity", "1")));
        try
        {
            using var ui = TestUi.Create(width: Width, height: Height);
            ui.LoadStyles(".fade { animation: cb-opacity-fade 0.6s ease-out both; width: 200px; height: 24px; color: #ffffff; }");
            var fade = new Panel { TagName = "div" };
            fade.AddClass("fade");
            fade.AddChild(new Label("HELLO"));
            ui.Screen.AddChild(fade);
            ui.Render();
            var label = (Label)fade.Children[0];

            // Mid-animation the child's inherited opacity tracks the parent's
            // composed (animated) opacity.
            ui.Update(0.1f);
            ui.Render();
            Assert.Equal(fade.ComputedStyle.Opacity, label.ComputedStyle.Opacity, 3);

            // After the entrance completes (fill both → opacity 1), the label
            // must be opaque and actually painted at its layout rect. The last
            // render is a partial redraw, so fully-opaque glyph cores prove the
            // inherited opacity reached the child on the incremental path
            // (sampling a row can hit antialiased edges, hence the max alpha).
            for (var i = 0; i < 12; i++)
            {
                ui.Update(0.1f);
                ui.Render();
            }
            Assert.Equal(1f, fade.ComputedStyle.Opacity, 3);
            Assert.Equal(1f, label.ComputedStyle.Opacity, 3);

            var pixels = ui.Render().ToArray();
            var labelRect = label.Layout;
            var maxAlpha = 0;
            for (var y = (int)labelRect.Y; y < labelRect.Bottom; y++)
            for (var x = (int)labelRect.X; x < labelRect.Right; x++)
            {
                var i = (y * Width + x) * 4;
                if (pixels[i + 3] > maxAlpha) maxAlpha = pixels[i + 3];
            }
            Assert.True(maxAlpha > 250, "animated parent's text child is not painted at full opacity after the entrance animation");
        }
        finally
        {
            Keyframes.Remove("cb-opacity-fade");
        }
    }

    [Fact]
    public void IdentityTransformChildrenSurvivePartialRedraw()
    {
        // Regression: `transform: translate(0px, 0px)` composed an identity
        // matrix but still routed through the transformed-paint path, which
        // draws children in local coordinates. The partial-raster cull then
        // compared those local rects against screen-space damage and skipped
        // the label, erasing it on the first paint-only redraw.
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.LoadStyles("""
            .wrap { width: 140px; height: 34px; margin: 20px; background-color: #3478d4;
                    transform: translate(0px, 0px); color: #ffffff; align-items: center; justify-content: center; }
            """);
        var wrap = new Panel { TagName = "div" };
        wrap.AddClass("wrap");
        wrap.AddChild(new Label("HELLO"));
        ui.Screen.AddChild(wrap);
        ui.Render();
        Assert.False(wrap.ComputedStyle.HasTransform, "an identity transform must not count as a transform");

        var label = (Label)wrap.Children[0];
        var labelRect = label.Layout;

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

        Assert.True(WhiteIn(ui.Render().ToArray(), Width, (int)labelRect.X, (int)labelRect.Y, (int)labelRect.Right, (int)labelRect.Bottom) > 4,
            "label not painted on the initial render");

        // An unrelated paint-only invalidation triggers a partial redraw; the
        // label inside the identity-transform panel must stay visible.
        wrap.InvalidatePaint();
        var partial = ui.Render().ToArray();
        Assert.True(WhiteIn(partial, Width, (int)labelRect.X, (int)labelRect.Y, (int)labelRect.Right, (int)labelRect.Bottom) > 4,
            "label erased by the partial redraw (identity transform culling bug)");
    }
}
