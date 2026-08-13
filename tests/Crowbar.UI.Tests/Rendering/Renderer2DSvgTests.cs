using System.Numerics;
using Crowbar.Engine.Rendering2D;
using Xunit;

namespace Crowbar.Engine.Tests;

public class Renderer2DSvgTests
{
    [Fact]
    public void FilledSquare_EmitsTrianglesInsideDest()
    {
        var renderer = new Renderer2D();
        renderer.Begin(200, 200);
        var svg = SvgDocumentParser.Parse(
            "<svg viewBox='0 0 10 10' fill='currentColor'><rect width='10' height='10'/></svg>");
        renderer.DrawSvg(svg, new RectF(50, 50, 100, 100), ColorF.FromRgba(255, 0, 0));
        renderer.End();

        Assert.True(renderer.TriangleCount >= 6); // square -> 2 triangles
        foreach (var vertex in renderer.Triangles)
        {
            Assert.InRange(vertex.Position.X, 50f, 150f);
            Assert.InRange(vertex.Position.Y, 50f, 150f);
        }
        var command = Assert.Single(renderer.Commands);
        Assert.Equal(BatchKind.Triangles, command.Kind);
    }

    [Fact]
    public void StrokedPath_EmitsLineInstances()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        var svg = SvgDocumentParser.Parse(
            "<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.8'>" +
            "<path d='M12 5v14M5 12h14'/></svg>");
        renderer.DrawSvg(svg, new RectF(0, 0, 24, 24), ColorF.White);
        renderer.End();

        // Two open subpaths -> two round-capped line instances.
        Assert.Equal(2, renderer.InstanceCount);
        foreach (var instance in renderer.Instances)
            Assert.Equal(3f, instance.Flags.X); // line kind
    }

    [Fact]
    public void ContainScaling_CentersViewBox()
    {
        var renderer = new Renderer2D();
        renderer.Begin(200, 200);
        // A 10x20 viewBox drawn into a 100x100 square: scale 5, centered.
        var svg = SvgDocumentParser.Parse(
            "<svg viewBox='0 0 10 20' fill='currentColor'><rect width='10' height='20'/></svg>");
        renderer.DrawSvg(svg, new RectF(0, 0, 100, 100), ColorF.White);
        renderer.End();

        foreach (var vertex in renderer.Triangles)
        {
            // width 50 (10*5), centered -> x in [25, 75]; height fills y in [0, 100].
            Assert.InRange(vertex.Position.X, 25f, 75f);
            Assert.InRange(vertex.Position.Y, 0f, 100f);
        }
    }

    [Fact]
    public void FillWithHole_EvenOddEmitsBothRings()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        var svg = SvgDocumentParser.Parse(
            "<svg viewBox='0 0 10 10' fill-rule='evenodd' fill='currentColor'>" +
            "<path d='M0 0h10v10H0zM2 2h6v6H2z'/></svg>");
        renderer.DrawSvg(svg, new RectF(0, 0, 100, 100), ColorF.White);
        renderer.End();

        // Outer 100*100 minus inner 60*60 = 6400 covered area (in dest pixels).
        Assert.Equal(6400f, TriangleArea(renderer.Triangles), precision: 1);
    }

    [Fact]
    public void Tint_AppliesToCurrentColor()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        var svg = SvgDocumentParser.Parse(
            "<svg viewBox='0 0 10 10' fill='currentColor'><rect width='10' height='10'/></svg>");
        renderer.DrawSvg(svg, new RectF(0, 0, 10, 10), ColorF.FromRgba(0, 0, 255));
        renderer.End();

        Assert.All(renderer.Triangles, vertex => Assert.Equal(new Vector4(0, 0, 1, 1), vertex.Color));
    }

    [Fact]
    public void DrawSvg_RespectsClip()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.PushClip(new RectF(0, 0, 10, 10));
        var svg = SvgDocumentParser.Parse(
            "<svg viewBox='0 0 10 10' fill='currentColor'><rect width='10' height='10'/></svg>");
        renderer.DrawSvg(svg, new RectF(0, 0, 50, 50), ColorF.White);
        renderer.End();

        // The 50x50 fill is clipped to the 10x10 clip rect.
        Assert.Equal(100f, TriangleArea(renderer.Triangles), precision: 1);
    }

    private static float TriangleArea(IReadOnlyList<TriVertex> triangles)
    {
        var area = 0f;
        for (var i = 0; i + 2 < triangles.Count; i += 3)
        {
            var a = triangles[i].Position;
            var b = triangles[i + 1].Position;
            var c = triangles[i + 2].Position;
            area += MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) * 0.5f;
        }
        return area;
    }
}
