using System.Numerics;
using Crowbar.Engine.Rendering2D;
using Xunit;

namespace Crowbar.Engine.Tests;

/// <summary>
/// Headless tests of the text path: the multi-channel signed distance field
/// (MSDF), glyph rasterization and <see cref="Renderer2D.DrawText"/> quad
/// emission. They use a real system font (via <see cref="FontManager"/>'s
/// fallback chain) so the measured glyph stream is real, without needing a GPU.
/// </summary>
public class Renderer2DTextTests
{
    // --- Median reconstruction ----------------------------------------------

    [Fact]
    public void Median_Reconstruction_MatchesShaderMath()
    {
        // The WGSL fragment shader reconstructs the distance as the median of
        // the three stored channels; this mirrors that math exactly.
        Assert.Equal(0.53125f, Median(0.53125f, 0.8125f, 0.53125f), 6);
        Assert.Equal(0.46875f, Median(0.46875f, 0.1875f, 0.46875f), 6);
        Assert.Equal(0.5f, Median(0.5f, 0.5f, 0.5f), 6);
        // When two channels carry the true distance, the median equals it.
        Assert.Equal(0.625f, Median(0.625f, 0.8125f, 0.625f), 6);
        Assert.Equal(0.375f, Median(0.375f, 0.3125f, 0.375f), 6);
    }

    // --- Glyph rasterization ------------------------------------------------

    [Fact]
    public void Rasterize_Square_ProducesInOutSignedField()
    {
        // A 10x10 axis-aligned square (two triangles' worth of edges).
        Vector2[] edges =
        [
            new(0, 0), new(10, 0),
            new(10, 0), new(10, 10),
            new(10, 10), new(0, 10),
            new(0, 10), new(0, 0)
        ];

        var msdf = GlyphRasterizer.Rasterize(edges, Vector2.Zero, 10, 10);

        // The grid is rasterized at GlyphRasterizer.Scale× the ink size.
        var expected = (10 + GlyphRasterizer.Padding * 2) * GlyphRasterizer.Scale;
        Assert.Equal(expected, msdf.Width);
        Assert.Equal(expected, msdf.Height);

        // Center pixel is deep inside: stored median > 0.5.
        Assert.True(MedianStored(msdf, msdf.Width / 2, msdf.Height / 2) > 0.5f);
        // A far corner pixel is outside: stored median < 0.5.
        Assert.True(MedianStored(msdf, 0, 0) < 0.5f);
        // Alpha carries the true signed-distance fallback and is positive in
        // the center of the glyph.
        var centerIndex = (msdf.Height / 2 * msdf.Width + msdf.Width / 2) * 4;
        Assert.True(msdf.Pixels[centerIndex + 3] > 127);
    }

    [Fact]
    public void Rasterize_Square_ChannelsReconstructTrueDistance()
    {
        // A square has only two edge colors, so the third channel is filled
        // with the true distance. At ink (2, 5) the nearest edges are the left
        // (2px) and bottom (5px) edges: the median must reconstruct 2px, and
        // the stored channels must genuinely differ (multi-channel).
        Vector2[] edges =
        [
            new(0, 0), new(10, 0),
            new(10, 0), new(10, 10),
            new(10, 10), new(0, 10),
            new(0, 10), new(0, 0)
        ];

        var msdf = GlyphRasterizer.Rasterize(edges, Vector2.Zero, 10, 10);

        // Sample near ink (1.75, 4.75), where the nearest edge is the left
        // one. The expected value is computed from the actual cell center
        // below because the supersampling scale may not land exactly on those
        // display coordinates.
        var x = InkToGrid(1.75f);
        var y = InkToGrid(4.75f);
        var d = Distance(msdf, x, y);
        var sampledX = (x - GlyphRasterizer.Padding * GlyphRasterizer.Scale + 0.5f) / GlyphRasterizer.Scale;
        var sampledY = (y - GlyphRasterizer.Padding * GlyphRasterizer.Scale + 0.5f) / GlyphRasterizer.Scale;
        var expected = MathF.Min(sampledX, sampledY) * GlyphRasterizer.Scale;
        Assert.True(MathF.Abs(d - expected) < 0.3f, $"reconstructed {d}, expected ≈ {expected} grid units");
        var hasChannelSeparation = false;
        for (var pixel = 0; pixel < msdf.Pixels.Length; pixel += 4)
        {
            if (msdf.Pixels[pixel] != msdf.Pixels[pixel + 1] || msdf.Pixels[pixel + 1] != msdf.Pixels[pixel + 2])
            {
                hasChannelSeparation = true;
                break;
            }
        }
        Assert.True(hasChannelSeparation, "the MSDF must preserve channel separation somewhere in the field");
    }

    [Fact]
    public void Rasterize_Square_CornerStaysSharp()
    {
        // A 20x20 square. Near a convex corner the true (rounded) distance
        // follows sqrt(x² + y²); sampled and bilinearly filtered, that rounds
        // the corner. The MSDF median must track the perpendicular pseudo-
        // distance min(x, y) instead, keeping the corner sharp.
        Vector2[] edges =
        [
            new(0, 0), new(20, 0),
            new(20, 0), new(20, 20),
            new(20, 20), new(0, 20),
            new(0, 20), new(0, 0)
        ];

        var msdf = GlyphRasterizer.Rasterize(edges, Vector2.Zero, 20, 20);

        // The corner at ink (0, 0) sits between the first inside/outside cells.
        var cornerGrid = GlyphRasterizer.Padding * GlyphRasterizer.Scale;
        for (var offset = 4; offset >= 2; offset--)
        {
            var g = cornerGrid - offset;
            var t = offset - 0.5f;              // pseudo-distance in grid units
            var pseudo = -t;                    // min(x, y) in grid units
            var rounded = -MathF.Sqrt(2f) * t;  // sqrt(x² + y²) in grid units
            var d = Distance(msdf, g, g);
            Assert.True(MathF.Abs(d - pseudo) < 0.3f, $"texel ({g},{g}): got {d}, expected ≈ {pseudo}");
            Assert.True(
                MathF.Abs(d - pseudo) < MathF.Abs(d - rounded),
                $"texel ({g},{g}) corner is rounded: got {d}, pseudo {pseudo}, rounded {rounded}");
        }

        // The texel just outside the corner tracks the pseudo-distance too.
        var corner = Distance(msdf, cornerGrid - 1, cornerGrid - 1); // pseudo -0.5 grid
        Assert.True(MathF.Abs(corner + 0.5f) < 0.3f, $"corner texel: {corner}");
    }

    [Fact]
    public void Rasterize_CorrectsSupportingLineExtensionsOutsideCorners()
    {
        // The supporting line of the top edge extends indefinitely to the
        // left. Without comparing against the finite-segment distance, that
        // extension is incorrectly reconstructed as another glyph stroke.
        Vector2[] edges =
        [
            new(0, 0), new(20, 0),
            new(20, 0), new(20, 4),
            new(20, 4), new(0, 4),
            new(0, 4), new(0, 0)
        ];

        var msdf = GlyphRasterizer.Rasterize(edges, Vector2.Zero, 20, 4);
        var sampleY = GlyphRasterizer.Padding * GlyphRasterizer.Scale;
        // Grid (0, Padding*Scale) samples just outside the top-left endpoint.
        // Its true distance is about 2.9 px, not the 0.1 px distance to the
        // top edge's infinite supporting line.
        var distance = Distance(msdf, 0, sampleY);
        Assert.True(distance < -4.5f, $"line extension survived correction: {distance}");

    }

    [Fact]
    public void Rasterize_HollowSquare_HasHole()
    {
        // A square with a hole: outer contour + inner contour (even-odd).
        Vector2[] edges =
        [
            new(0, 0), new(10, 0), new(10, 0), new(10, 10),
            new(10, 10), new(0, 10), new(0, 10), new(0, 0),
            new(3, 3), new(3, 7), new(3, 7), new(7, 7),
            new(7, 7), new(7, 3), new(7, 3), new(3, 3)
        ];

        var msdf = GlyphRasterizer.Rasterize(edges, Vector2.Zero, 10, 10);

        // The band (outer square minus the hole) is filled; the hole center is
        // outside the shape. At these texels all channels coincide with the
        // true distance, so sampling any channel is valid.
        var band = Sample(msdf, InkToGrid(1.5f), InkToGrid(1.5f)); // near ink (1.25, 1.25)
        Assert.True(band > 0.5f);
        var hole = Sample(msdf, InkToGrid(5.5f), InkToGrid(5.5f)); // near ink (5.25, 5.25)
        Assert.True(hole < 0.5f);
    }

    // --- DrawText -----------------------------------------------------------

    [Fact]
    public void DrawText_EmitsGlyphQuads()
    {
        var renderer = new Renderer2D();
        renderer.Begin(400, 200);
        renderer.DrawText("Hi", new Vector2(10, 10), 24f, ColorF.White);
        renderer.End();

        Assert.True(renderer.TexturedCount > 0);
        Assert.Equal(0, renderer.TexturedCount % 6);
        Assert.True(renderer.GlyphCacheCount > 0);
        // The quads are drawn through the glyph batch.
        Assert.Contains(renderer.Commands, c => c.Kind == BatchKind.Glyph);
    }

    [Fact]
    public void DrawText_UsesDistinctLaidOutGlyphOrigins()
    {
        var renderer = new Renderer2D();
        renderer.Begin(400, 200);
        renderer.DrawText("FillLight", new Vector2(10, 10), 14f, ColorF.White);
        renderer.End();

        var origins = renderer.TexturedVerts
            .Chunk(6)
            .Select(quad => quad[0].Position.X)
            .ToArray();

        Assert.Equal("FillLight".Length, origins.Length);
        Assert.All(origins.Zip(origins.Skip(1)), pair => Assert.True(pair.Second > pair.First));
    }

    [Fact]
    public void DrawText_CachesGlyphsAcrossDraws()
    {
        var renderer = new Renderer2D();
        renderer.Begin(400, 200);
        renderer.DrawText("Hello", new Vector2(10, 10), 20f, ColorF.White);
        renderer.End();
        var cached = renderer.GlyphCacheCount;

        renderer.Begin(400, 200);
        renderer.DrawText("Hello", new Vector2(10, 40), 20f, ColorF.White);
        renderer.End();

        // The same glyphs are reused; no new atlas entries were created.
        Assert.Equal(cached, renderer.GlyphCacheCount);
    }

    [Fact]
    public void DrawText_ReplayShiftsQuadsByPosition()
    {
        var renderer = new Renderer2D();
        renderer.Begin(400, 200);
        renderer.DrawText("Hello", new Vector2(10, 10), 20f, ColorF.White);
        renderer.End();
        var first = renderer.TexturedVerts.ToArray();
        Assert.NotEmpty(first);

        // Second draw of the same text at a different position must replay the
        // cached shaped run (no SixLabors re-shaping) and land exactly dx/dy
        // away from the first draw.
        renderer.Begin(400, 200);
        renderer.DrawText("Hello", new Vector2(35, 60), 20f, ColorF.White);
        renderer.End();
        var second = renderer.TexturedVerts.ToArray();
        Assert.Equal(first.Length, second.Length);
        for (var i = 0; i < first.Length; i++)
        {
            Assert.Equal(first[i].Position.X + 25f, second[i].Position.X, 3);
            Assert.Equal(first[i].Position.Y + 50f, second[i].Position.Y, 3);
        }
    }

    [Fact]
    public void DrawText_BakesColorIntoQuads()
    {
        var renderer = new Renderer2D();
        renderer.Begin(400, 200);
        var color = ColorF.FromRgba(255, 0, 0, 128);
        renderer.DrawText("A", new Vector2(10, 10), 24f, color);
        renderer.End();

        Assert.NotEmpty(renderer.TexturedVerts);
        Assert.All(renderer.TexturedVerts, v => Assert.Equal(new Vector4(1, 0, 0, 128f / 255f), v.Color));
    }

    [Fact]
    public void DrawText_Centered_OffsetsFirstGlyphRightward()
    {
        var renderer = new Renderer2D();
        renderer.Begin(400, 200);
        var style = new TextStyle(24f, ColorF.White, align: TextAlign.Center, maxWidth: 300f);
        renderer.DrawText("ii", new Vector2(0, 10), style);
        renderer.End();

        Assert.NotEmpty(renderer.TexturedVerts);
        // Centered text within a 300px box cannot start at x = 0 for a narrow string.
        Assert.True(renderer.TexturedVerts[0].Position.X > 0f);
    }

    [Fact]
    public void DrawText_Centered_CentersTheGlyphRunWithinTheBox()
    {
        var renderer = new Renderer2D();
        renderer.Begin(400, 200);
        var style = new TextStyle(24f, ColorF.White, align: TextAlign.Center, maxWidth: 300f);
        renderer.DrawText("ii", new Vector2(0, 10), style);
        renderer.End();

        Assert.NotEmpty(renderer.TexturedVerts);
        var minX = renderer.TexturedVerts.Min(v => v.Position.X);
        var maxX = renderer.TexturedVerts.Max(v => v.Position.X);
        var center = (minX + maxX) / 2f;
        // The glyph run must sit around the box midpoint (150). The old path
        // shifted the origin by the centering offset *and* let SixLabors center
        // again, pushing narrow text far past the right edge (~292).
        Assert.InRange(center, 145f, 155f);
    }

    [Fact]
    public void DrawText_RightAligned_EndsTheGlyphRunAtTheBoxRightEdge()
    {
        var renderer = new Renderer2D();
        renderer.Begin(400, 200);
        var style = new TextStyle(24f, ColorF.White, align: TextAlign.Right, maxWidth: 300f);
        renderer.DrawText("ii", new Vector2(0, 10), style);
        renderer.End();

        Assert.NotEmpty(renderer.TexturedVerts);
        var maxX = renderer.TexturedVerts.Max(v => v.Position.X);
        // Right-aligned text must end near the 300px box edge, not be pushed
        // past it by a double-applied end offset.
        Assert.InRange(maxX, 295f, 305f);
    }

    [Fact]
    public void DrawText_EmptyString_EmitsNothing()
    {
        var renderer = new Renderer2D();
        renderer.Begin(400, 200);
        renderer.DrawText("", new Vector2(10, 10), 24f, ColorF.White);
        renderer.End();

        Assert.Equal(0, renderer.TexturedCount);
    }

    [Fact]
    public void GlyphShader_ExposesExpectedEntryPointsAndBindings()
    {
        var shader = Shader.Load("Shaders/Glyph.wgsl");
        Assert.Contains(shader.EntryPoints, e => e.Name == "vs_main");
        Assert.Contains(shader.EntryPoints, e => e.Name == "fs_main");
        Assert.Equal(3, shader.Bindings.Count);
        Assert.Contains(shader.Bindings, b => b.Slot == 0 && b.Kind == ShaderBindingKind.Texture);
        Assert.Contains(shader.Bindings, b => b.Slot == 1 && b.Kind == ShaderBindingKind.Sampler);
        Assert.Contains(shader.Bindings, b => b.Slot == 2 && b.Kind == ShaderBindingKind.UniformBuffer);
    }

    // --- Helpers ------------------------------------------------------------

    /// <summary>The median of three channels, exactly as the glyph shader computes it.</summary>
    private static float Median(float r, float g, float b) =>
        MathF.Max(MathF.Min(r, g), MathF.Min(MathF.Max(r, g), b));

    /// <summary>Reconstructed distance (grid units) at a texel: median-of-three mapped back through the range.</summary>
    private static float Distance(Crowbar.Engine.Texture2D msdf, int x, int y)
    {
        var i = (y * msdf.Width + x) * 4;
        var r = msdf.Pixels[i] / 255f;
        var g = msdf.Pixels[i + 1] / 255f;
        var b = msdf.Pixels[i + 2] / 255f;
        return (Median(r, g, b) - 0.5f) * (GlyphRasterizer.Spread * GlyphRasterizer.Scale);
    }

    private static float MedianStored(Crowbar.Engine.Texture2D msdf, int x, int y)
    {
        var i = (y * msdf.Width + x) * 4;
        return Median(
            msdf.Pixels[i] / 255f,
            msdf.Pixels[i + 1] / 255f,
            msdf.Pixels[i + 2] / 255f);
    }

    private static float Sample(Crowbar.Engine.Texture2D sdf, int x, int y) =>
        sdf.Pixels[(y * sdf.Width + x) * 4] / 255f;

    // Grid index whose cell center lands on the given ink coordinate.
    private static int InkToGrid(float ink) =>
        (int)MathF.Round(ink * GlyphRasterizer.Scale + GlyphRasterizer.Padding * GlyphRasterizer.Scale - 0.5f);
}
