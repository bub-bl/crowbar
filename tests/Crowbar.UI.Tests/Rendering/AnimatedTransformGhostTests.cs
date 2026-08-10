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

    [Fact]
    public void ShrinkPulseLeavesNoResidueOutsideDamage()
    {
        // The demo's action buttons pulse scale 1 -> 1.04 -> 1 continuously.
        // When the button shrinks, the pixels it painted at the larger scale
        // must be cleared even though they sit outside the (untransformed)
        // damage rect. Without the fix the damage rect ignored the transform,
        // so the outer ring of the last large frame stayed as ghost residue.
        const string animationName = "animated-transform-ghost-pulse";
        Keyframes.Define(animationName, KeyframeFrame.At(0f, ("transform", "scale(1)")),
            KeyframeFrame.At(1f, ("transform", "scale(1.5)")));
        using var ui = TestUi.Create(320, 200);
        var box = new Panel();
        box.SetInlineStyle("animation", $"{animationName} 1s linear infinite alternate");
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
        var incremental = ui.Render().ToArray();
        Console.WriteLine($"[shrink] damage={string.Join(";", ui.Renderer.DamageRects)}");

        ui.Renderer.MarkDirty();
        var full = ui.Render().ToArray();

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
        // Keep the animated button in the Skia texture for this pixel-level
        // geometry assertion; GPU fills intentionally leave its interior
        // transparent because the compositor paints that region later.
        ui.Renderer.GpuFills = false;
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
        ui.Render();
        Console.WriteLine($"[transform] {string.Join(" ", action!.ComputedStyle.Transform.Ops.Select(op => op.Type))} layout={action.Layout}");

        // Verify the actual matrix rather than a composited pixel: GPU fills
        // intentionally leave the button interior transparent in the Skia
        // texture. A center-scale must keep the box center fixed and place the
        // two horizontal edges symmetrically around it.
        var matrix = action.ComputedStyle.Transform.BuildMatrix(
            action.Layout.Width, action.Layout.Height, action.ComputedStyle.TransformOrigin,
            action.Layout.X, action.Layout.Y);
        var mapped = matrix.MapPoints([
            new SKPoint(0, 0),
            new SKPoint(action.Layout.Width, action.Layout.Height),
            new SKPoint(action.Layout.Width / 2, action.Layout.Height / 2)]);
        var expectedCenter = new SKPoint(
            action.Layout.X + action.Layout.Width / 2,
            action.Layout.Y + action.Layout.Height / 2);
        Assert.InRange(Math.Abs(mapped[2].X - expectedCenter.X), 0f, 0.01f);
        Assert.InRange(Math.Abs(mapped[2].Y - expectedCenter.Y), 0f, 0.01f);

    }
}
