using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// Reproduces the hover ghosting seen on the /filters demo page: after a
/// paint-only invalidation (pointer hover), the incremental render must match
/// a full redraw within a perceptual tolerance. The raster bitmap is the
/// source of truth and each frame only the reported damage rects are copied
/// into the (persistent) GPU texture, so a stale or mispositioned damage rect
/// accumulates ghosted content even when every individual bitmap is correct.
/// </summary>
public class FilterHoverGhostTests
{
    private const int PerceptualAlphaTolerance = 40;

    [Fact]
    public void HoverOnFiltersDemoLeavesNoResidue()
    {
        var uiDir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "src", "Editor", "Ui"));
        Assert.True(Directory.Exists(uiDir), $"Ui directory not found: {uiDir}");

        using var ui = new UiSystem();
        ui.SetViewport(1280, 720);
        ui.Renderer.GpuFills = true;
        ui.Renderer.GpuDecorations = true;
        ui.RegisterRazorComponentsFromDirectory(uiDir);
        ui.Navigate("/filters");
        ui.Render();

        // Dense scan: hover every grid point so any "certain spot" that ghosts
        // is exercised. The hovered page has no hover rules in most of the
        // scanned area, so the incremental repaint must equal a full redraw
        // apart from sub-perceptual antialias noise at clip boundaries.
        var ghosts = new List<(int X, int Y, int Diff, byte MaxAlphaDelta)>();
        for (var gy = 8; gy < 720; gy += 40)
        {
            for (var gx = 8; gx < 1280; gx += 40)
            {
                ui.ProcessPointerMove(gx, gy);
                var incremental = ui.Render().ToArray();
                ui.Renderer.MarkDirty();
                var full = ui.Render().ToArray();

                var (d0, maxDelta) = Compare(incremental, full);
                if (d0 > 0) ghosts.Add((gx, gy, d0, maxDelta));
            }
        }

        foreach (var g in ghosts.OrderByDescending(g => g.MaxAlphaDelta).ThenByDescending(g => g.Diff).Take(5))
            Console.WriteLine($"[ghost@{g.X},{g.Y}] {g.Diff}px maxAlphaDelta={g.MaxAlphaDelta}");

        var failing = ghosts.Where(g => g.MaxAlphaDelta > PerceptualAlphaTolerance).ToList();
        Assert.True(failing.Count == 0,
            $"hover left visible residue ({failing.Count} points, worst: {string.Join("; ", failing.OrderByDescending(g => g.MaxAlphaDelta).Take(3).Select(g => $"({g.X},{g.Y}) {g.Diff}px Δ{g.MaxAlphaDelta}"))})");
    }

    private static (int Diff, byte MaxAlphaDelta) Compare(ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b)
    {
        var sa = a.Span;
        var sb = b.Span;
        var diff = 0;
        var max = 0;
        for (var i = 0; i < sa.Length; i += 4)
        {
            if (sa[i] != sb[i] || sa[i + 1] != sb[i + 1] || sa[i + 2] != sb[i + 2] || sa[i + 3] != sb[i + 3])
                diff++;
            var delta = Math.Abs(sa[i + 3] - sb[i + 3]);
            if (delta > max) max = delta;
        }

        return (diff, (byte)max);
    }
}
