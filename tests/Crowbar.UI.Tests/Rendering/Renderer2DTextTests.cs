using System.Numerics;
using Crowbar.Engine.Rendering2D;
using Xunit;

namespace Crowbar.Engine.Tests;

/// <summary>
/// Headless tests of the text path: the signed distance field, glyph
/// rasterization and <see cref="Renderer2D.DrawText"/> quad emission. They use
/// a real system font (via <see cref="FontManager"/>'s fallback chain) so the
/// measured glyph stream is real, without needing a GPU.
/// </summary>
public class Renderer2DTextTests
{
    // --- Distance field ----------------------------------------------------

    [Fact]
    public void DistanceField_SingleCell_CenterPositiveEdgeNegative()
    {
        // 3x3 mask with only the center cell inside.
        var inside = new bool[9];
        inside[4] = true;

        var signed = DistanceField.Signed(inside, 3, 3);

        Assert.Equal(0.5f, signed[4], 3);            // center: +0.5 (adjacent to boundary)
        Assert.Equal(-0.5f, signed[1], 3);           // top neighbor: -0.5
        Assert.Equal(-0.5f, signed[3], 3);           // left neighbor: -0.5
        Assert.Equal(-(MathF.Sqrt(2f) - 0.5f), signed[0], 3); // corner: -sqrt(2)+0.5
    }

    [Fact]
    public void DistanceField_SolidBlock_InteriorDistancesGrow()
    {
        // 5x5 mask with a 3x3 inside block at the center.
        var inside = new bool[25];
        for (var y = 1; y <= 3; y++)
        for (var x = 1; x <= 3; x++)
            inside[y * 5 + x] = true;

        var signed = DistanceField.Signed(inside, 5, 5);

        Assert.Equal(1.5f, signed[2 * 5 + 2], 3); // block center: 2 cells to the boundary - 0.5
        Assert.Equal(0.5f, signed[1 * 5 + 2], 3); // top-middle inside: adjacent to boundary
        Assert.Equal(-0.5f, signed[0 * 5 + 2], 3); // just outside the top edge
        Assert.Equal(-(MathF.Sqrt(2f) - 0.5f), signed[0 * 5 + 0], 3); // corner
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

        var sdf = GlyphRasterizer.Rasterize(edges, Vector2.Zero, 10, 10);

        // The grid is rasterized at GlyphRasterizer.Scale× the ink size.
        var expected = (10 + GlyphRasterizer.Padding * 2) * GlyphRasterizer.Scale;
        Assert.Equal(expected, sdf.Width);
        Assert.Equal(expected, sdf.Height);

        // Center pixel is deep inside: stored value > 0.5.
        var center = Sample(sdf, sdf.Width / 2, sdf.Height / 2);
        Assert.True(center > 0.5f);
        // A corner pixel is far outside: stored value < 0.5.
        var corner = Sample(sdf, 0, 0);
        Assert.True(corner < 0.5f);
        // The stored SDF is single-channel: RGB equal, alpha opaque.
        var centerIndex = (sdf.Height / 2 * sdf.Width + sdf.Width / 2) * 4;
        Assert.Equal(sdf.Pixels[centerIndex], sdf.Pixels[centerIndex + 1]);
        Assert.Equal(sdf.Pixels[centerIndex], sdf.Pixels[centerIndex + 2]);
        Assert.Equal(255, sdf.Pixels[centerIndex + 3]);
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

        var sdf = GlyphRasterizer.Rasterize(edges, Vector2.Zero, 10, 10);

        // Grid coordinates map to ink via
        // ink = (grid - Padding*Scale + 0.5) / Scale. The band (outer square
        // minus the hole) is filled; the hole center is outside.
        var band = Sample(sdf, InkToGrid(1.5f), InkToGrid(1.5f)); // ink (1.5, 1.5): inside the filled band
        Assert.True(band > 0.5f);
        var hole = Sample(sdf, InkToGrid(5.5f), InkToGrid(5.5f)); // ink (5.5, 5.5): inside the hole
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

    private static float Sample(Crowbar.Engine.Texture2D sdf, int x, int y) =>
        sdf.Pixels[(y * sdf.Width + x) * 4] / 255f;

    // Grid index whose cell center lands on the given ink coordinate.
    private static int InkToGrid(float ink) =>
        (int)MathF.Round(ink * GlyphRasterizer.Scale + GlyphRasterizer.Padding * GlyphRasterizer.Scale - 0.5f);
}
