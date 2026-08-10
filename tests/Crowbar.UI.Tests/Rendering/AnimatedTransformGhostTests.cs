using Crowbar.UI;
using SkiaSharp;
using Xunit;

namespace Crowbar.UI.Tests.Rendering;

public class AnimatedTransformGhostTests
{
    private static int DiffCount(ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b)
    {
        var sa = a.Span;
        var sb = b.Span;
        var diff = 0;
        for (var i = 0; i < sa.Length; i += 4)
            if (sa[i] != sb[i] || sa[i + 1] != sb[i + 1] || sa[i + 2] != sb[i + 2] || sa[i + 3] != sb[i + 3])
                diff++;
        return diff;
    }

    private static (byte, byte, byte, byte) Pixel(ReadOnlyMemory<byte> pixels, int width, int x, int y)
    {
        var span = pixels.Span;
        var o = (y * width + x) * 4;
        return (span[o], span[o + 1], span[o + 2], span[o + 3]);
    }

    [Fact]
    public void ShrinkPulseLeavesNoResidueOutsideDamage()
    {
        // The demo's action buttons pulse scale 1 -> 1.04 -> 1 continuously.
        // When the button shrinks, the pixels it painted at the larger scale
        // must be cleared even though they sit outside the (untransformed)
        // damage rect. Without the fix the damage rect ignored the transform,
        // so the outer ring of the last large frame stayed as ghost residue.
        Keyframes.Clear();
        Keyframes.Define("pulse", KeyframeFrame.At(0f, ("transform", "scale(1)")),
            KeyframeFrame.At(1f, ("transform", "scale(1.5)")));
        using var ui = TestUi.Create(320, 200);
        var box = new Panel();
        box.SetInlineStyle("animation", "pulse 1s linear infinite alternate");
        box.SetInlineStyle("width", "80px");
        box.SetInlineStyle("height", "40px");
        box.SetInlineStyle("background-color", "#ff0000");
        ui.Screen.AddChild(box);
        ui.Render();

        // Grow to the largest scale.
        ui.Update(1f);
        ui.Render();
        Console.WriteLine($"[grow] damage={string.Join(";", ui.Renderer.DamageRects)}");

        // Shrink back to scale 1: the ring painted at 1.5 must vanish.
        ui.Update(0.5f); // elapsed 1.5 -> second iteration, progress 0.5 -> scale 1.25
        ui.Render();
        ui.Update(0.5f); // elapsed 2.0 -> back to scale 1
        var incremental = ui.Render();
        Console.WriteLine($"[shrink] damage={string.Join(";", ui.Renderer.DamageRects)}");

        ui.Renderer.MarkDirty();
        var full = ui.Render();

        var diff = DiffCount(incremental, full);
        Console.WriteLine($"[diff] {diff} px");
        Assert.Equal(0, diff);
    }

    [Fact]
    public void ScaledButtonStaysCenteredDuringPulse()
    {
        // The button must grow around its center: at scale 1.04 the painted
        // box is still centered on the layout rect, not drifted down-right.
        var uiDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Editor", "Ui"));
        using var ui = new UiSystem();
        ui.Screen.SetViewport(1280, 720);
        ui.Renderer.Resize(1280, 720);
        ui.RegisterRazorComponentFromFile("Demo", Path.Combine(uiDir, "Demo.razor"), "Demo");
        ui.Renderer.GpuDecorations = true;
        ui.Renderer.GpuFills = true;
        ui.Navigate("/demo");
        ui.Render();

        Panel? action = null;
        void Walk(Panel? p)
        {
            if (p is null) return;
            if (p.Classes.Contains("action") && action is null) action = p;
            foreach (var c in p.Children) Walk(c);
        }
        Walk(ui.Content);
        Assert.NotNull(action);
        for (var step = 0; step < 6; step++) ui.Update(0.1f);
        var pixels = ui.Render();
        var scale = action!.ComputedStyle.Transform.Ops.FirstOrDefault(o => o.Type == TransformOpType.Scale).A;
        Console.WriteLine($"[scale] {scale} layout={action.Layout}");

        // At scale ~1.04 around the center (125, 144): the box grows ~3px on
        // each side. The pixel just above the button top edge (y = layout.Y - 2)
        // must stay background; the first painted row must be within the
        // layout rect, not shifted 6px down.
        var midX = (int)(action.Layout.X + action.Layout.Width / 2);
        Assert.Equal(0, Pixel(pixels, 1280, midX, (int)action.Layout.Y - 3).Item4); // above the button: transparent
        Assert.True(Pixel(pixels, 1280, midX, (int)action.Layout.Y).Item4 > 0, "button top must be painted at its layout top");
        // Bottom-right: the scale grows symmetrically, so the pixel just below
        // the layout bottom at the center must still be background (scale 1.04
        // adds ~0.7px, not 6px).
        Assert.Equal(0, Pixel(pixels, 1280, midX, (int)action.Layout.Bottom + 3).Item4);
    }
}
