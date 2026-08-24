using System.Numerics;
using Crowbar.Engine.Rendering2D;
using Xunit;

namespace Crowbar.Engine.Tests;

public class SvgPathParserTests
{
    [Fact]
    public void AbsoluteMoveLine_Close_Parses()
    {
        var segments = SvgPathParser.Parse("M0 0 L10 0 L10 10 Z");

        Assert.Equal(4, segments.Count);
        Assert.Equal(SvgSegmentKind.Move, segments[0].Kind);
        Assert.Equal(new Vector2(0, 0), segments[0].P);
        Assert.Equal(SvgSegmentKind.Line, segments[1].Kind);
        Assert.Equal(new Vector2(10, 0), segments[1].P);
        Assert.Equal(SvgSegmentKind.Line, segments[2].Kind);
        Assert.Equal(new Vector2(10, 10), segments[2].P);
        Assert.Equal(SvgSegmentKind.Close, segments[3].Kind);
    }

    [Fact]
    public void RelativeCommands_Accumulate()
    {
        var segments = SvgPathParser.Parse("m10 10 l5 0 l0 5 z");

        Assert.Equal(SvgSegmentKind.Move, segments[0].Kind);
        Assert.Equal(new Vector2(10, 10), segments[0].P);
        Assert.Equal(new Vector2(15, 10), segments[1].P);
        Assert.Equal(new Vector2(15, 15), segments[2].P);
        Assert.Equal(SvgSegmentKind.Close, segments[3].Kind);
    }

    [Fact]
    public void MoveWithImplicitLineTo_EmitsLines()
    {
        var segments = SvgPathParser.Parse("M0 0 10 0 10 10");

        Assert.Equal(SvgSegmentKind.Move, segments[0].Kind);
        Assert.Equal(SvgSegmentKind.Line, segments[1].Kind);
        Assert.Equal(new Vector2(10, 0), segments[1].P);
        Assert.Equal(SvgSegmentKind.Line, segments[2].Kind);
        Assert.Equal(new Vector2(10, 10), segments[2].P);
    }

    [Fact]
    public void HorizontalVertical_Commands()
    {
        var segments = SvgPathParser.Parse("M0 0 H10 V10 h-5 v-5");

        Assert.Equal(SvgSegmentKind.Move, segments[0].Kind);
        Assert.Equal(new Vector2(10, 0), segments[1].P);
        Assert.Equal(new Vector2(10, 10), segments[2].P);
        Assert.Equal(new Vector2(5, 10), segments[3].P);
        Assert.Equal(new Vector2(5, 5), segments[4].P);
    }

    [Fact]
    public void CubicAndSmooth_ReflectControlPoint()
    {
        var segments = SvgPathParser.Parse("M0 0 C1 1 2 2 3 3 S4 4 5 5");

        Assert.Equal(SvgSegmentKind.Cubic, segments[1].Kind);
        Assert.Equal(new Vector2(1, 1), segments[1].P1);
        Assert.Equal(new Vector2(2, 2), segments[1].P2);
        Assert.Equal(new Vector2(3, 3), segments[1].P);

        Assert.Equal(SvgSegmentKind.Cubic, segments[2].Kind);
        // S reflects the previous cubic's second control point (2,2) around (3,3) -> (4,4).
        Assert.Equal(new Vector2(4, 4), segments[2].P1);
        Assert.Equal(new Vector2(4, 4), segments[2].P2);
        Assert.Equal(new Vector2(5, 5), segments[2].P);
    }

    [Fact]
    public void QuadraticAndSmooth_ReflectControlPoint()
    {
        var segments = SvgPathParser.Parse("M0 0 Q2 2 4 0 T8 0");

        Assert.Equal(SvgSegmentKind.Quadratic, segments[1].Kind);
        Assert.Equal(new Vector2(2, 2), segments[1].P1);
        Assert.Equal(new Vector2(4, 0), segments[1].P);

        Assert.Equal(SvgSegmentKind.Quadratic, segments[2].Kind);
        Assert.Equal(new Vector2(6, -2), segments[2].P1); // reflect (2,2) around (4,0)
        Assert.Equal(new Vector2(8, 0), segments[2].P);
    }

    [Fact]
    public void Arc_ParsesFlagsAndRadii()
    {
        var segments = SvgPathParser.Parse("M10 0 A10 10 0 1 1 -10 0");

        Assert.Equal(SvgSegmentKind.Arc, segments[1].Kind);
        Assert.Equal(10f, segments[1].P1.X);   // rx
        Assert.Equal(10f, segments[1].P1.Y);   // ry
        Assert.Equal(0f, segments[1].P2.X);    // rotation
        Assert.True(segments[1].LargeArc);
        Assert.True(segments[1].Sweep);
        Assert.Equal(new Vector2(-10, 0), segments[1].P);
    }

    [Fact]
    public void DecimalsAndExponents_Parse()
    {
        var segments = SvgPathParser.Parse("M1.5 -2.5 L5e0 .5");

        Assert.Equal(new Vector2(1.5f, -2.5f), segments[0].P);
        Assert.Equal(new Vector2(5f, 0.5f), segments[1].P);
    }
}

public class SvgFlattenerTests
{
    [Fact]
    public void Polyline_BecomesSingleClosedContour()
    {
        var contours = SvgFlattener.Flatten(SvgPathParser.Parse("M0 0 L10 0 L10 10 Z"));

        var contour = Assert.Single(contours);
        Assert.True(contour.Closed);
        Assert.Equal(3, contour.Points.Count);
        Assert.Equal(new Vector2(0, 0), contour.Points[0]);
        Assert.Equal(new Vector2(10, 10), contour.Points[2]);
    }

    [Fact]
    public void Move_StartsNewContour()
    {
        var contours = SvgFlattener.Flatten(SvgPathParser.Parse("M0 0 L10 0 M5 5 L5 10"));

        Assert.Equal(2, contours.Count);
        Assert.False(contours[0].Closed);
        Assert.False(contours[1].Closed);
    }

    [Fact]
    public void Cubic_IsSubdividedIntoPolyline()
    {
        var contours = SvgFlattener.Flatten(SvgPathParser.Parse("M0 0 C0 100 100 100 100 0"), 1f);

        var contour = Assert.Single(contours);
        Assert.True(contour.Points.Count > 2);
        Assert.Equal(new Vector2(0, 0), contour.Points[0]);
        Assert.Equal(new Vector2(100, 0), contour.Points[^1]);
    }

    [Fact]
    public void Arc_FlattensFromStartToEnd()
    {
        var contours = SvgFlattener.Flatten(SvgPathParser.Parse("M10 0 A10 10 0 1 1 -10 0"), 0.25f);

        var contour = Assert.Single(contours);
        Assert.True(contour.Points.Count > 2);
        Assert.Equal(new Vector2(10, 0), contour.Points[0]);
        var end = contour.Points[^1];
        Assert.Equal(-10f, end.X, precision: 2);
        Assert.Equal(0f, end.Y, precision: 2);
    }
}

public class EvenOddTriangulatorTests
{
    [Fact]
    public void Square_ProducesTwoTriangles()
    {
        var triangles = new List<Vector2>();
        EvenOddTriangulator.Triangulate(
            [[new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10)]],
            triangles);

        Assert.Equal(6, triangles.Count);
        Assert.Equal(100f, TotalArea(triangles), precision: 2);
    }

    [Fact]
    public void Triangle_ProducesOneTriangle()
    {
        var triangles = new List<Vector2>();
        EvenOddTriangulator.Triangulate(
            [[new Vector2(0, 0), new Vector2(10, 0), new Vector2(0, 10)]],
            triangles);

        Assert.Equal(3, triangles.Count);
        Assert.Equal(50f, TotalArea(triangles), precision: 2);
    }

    [Fact]
    public void Ring_FillsOuterMinusInner_UnderEvenOdd()
    {
        var triangles = new List<Vector2>();
        EvenOddTriangulator.Triangulate(
        [
            [new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10)],
            [new Vector2(2, 2), new Vector2(8, 2), new Vector2(8, 8), new Vector2(2, 8)]
        ], triangles);

        // Outer 100 minus inner 36 = 64.
        Assert.Equal(64f, TotalArea(triangles), precision: 2);
    }

    [Fact]
    public void DegenerateContour_IsIgnored()
    {
        var triangles = new List<Vector2>();
        EvenOddTriangulator.Triangulate([[new Vector2(0, 0), new Vector2(5, 0)]], triangles);
        Assert.Empty(triangles);
    }

    private static float TotalArea(List<Vector2> triangles)
    {
        var area = 0f;
        for (var i = 0; i + 2 < triangles.Count; i += 3)
        {
            var a = triangles[i];
            var b = triangles[i + 1];
            var c = triangles[i + 2];
            area += MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) * 0.5f;
        }
        return area;
    }
}

public class SvgDocumentParserTests
{
    [Fact]
    public void ViewBox_And_Path_AreParsed()
    {
        var shape = SvgDocumentParser.Parse(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='currentColor'>" +
            "<path d='M12 3l1.8 7.2L21 12l-7.2 1.8L12 21l-1.8-7.2L3 12l7.2-1.8z'/></svg>");

        Assert.Equal(new RectF(0, 0, 24, 24), shape.ViewBox);
        var element = Assert.Single(shape.Elements);
        Assert.Equal(SvgPaintKind.Tint, element.Fill.Kind);
        Assert.Equal(SvgPaintKind.None, element.Stroke.Kind);
        var contour = Assert.Single(element.Contours);
        Assert.True(contour.Closed);
        Assert.True(contour.Points.Count >= 4);
    }

    [Fact]
    public void Circle_InheritsStrokeFromRoot()
    {
        var shape = SvgDocumentParser.Parse(
            "<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.8'>" +
            "<circle cx='12' cy='12' r='8'/></svg>");

        var element = Assert.Single(shape.Elements);
        Assert.Equal(SvgPaintKind.None, element.Fill.Kind);
        Assert.Equal(SvgPaintKind.Tint, element.Stroke.Kind);
        Assert.Equal(1.8f, element.StrokeWidth);
        var contour = Assert.Single(element.Contours);
        Assert.True(contour.Closed);
        Assert.Equal(64, contour.Points.Count);
    }

    [Fact]
    public void Rect_BecomesFourPointContour()
    {
        var shape = SvgDocumentParser.Parse(
            "<svg viewBox='0 0 10 10' fill='#ff0000'><rect x='1' y='2' width='4' height='5'/></svg>");

        var element = Assert.Single(shape.Elements);
        Assert.Equal(SvgPaintKind.Literal, element.Fill.Kind);
        Assert.Equal(new ColorF(1f, 0f, 0f, 1f), element.Fill.Color);
        var contour = Assert.Single(element.Contours);
        Assert.Equal(4, contour.Points.Count);
        Assert.Equal(new Vector2(1, 2), contour.Points[0]);
    }

    [Fact]
    public void RoundedRect_InterleavesArcsAndEdges()
    {
        var shape = SvgDocumentParser.Parse(
            "<svg viewBox='0 0 10 10' fill='currentColor'><rect x='0' y='0' width='10' height='10' rx='2'/></svg>");

        var contour = Assert.Single(Assert.Single(shape.Elements).Contours);
        Assert.True(contour.Closed);
        Assert.True(contour.Points.Count > 4);
    }

    [Fact]
    public void Group_InheritsAndPropagates()
    {
        var shape = SvgDocumentParser.Parse(
            "<svg viewBox='0 0 10 10'><g fill='#00ff00'><path d='M0 0h10v10z'/></g></svg>");

        var element = Assert.Single(shape.Elements);
        Assert.Equal(SvgPaintKind.Literal, element.Fill.Kind);
        Assert.Equal(new ColorF(0f, 1f, 0f, 1f), element.Fill.Color);
    }

    [Fact]
    public void LiteralStrokeColor_OverridesTint()
    {
        var shape = SvgDocumentParser.Parse(
            "<svg viewBox='0 0 10 10' fill='none' stroke='#123456' stroke-width='2'>" +
            "<line x1='0' y1='0' x2='10' y2='10'/></svg>");

        var element = Assert.Single(shape.Elements);
        Assert.Equal(SvgPaintKind.Literal, element.Stroke.Kind);
        Assert.Equal(2f, element.StrokeWidth);
        Assert.Equal(2, element.Contours[0].Points.Count);
        Assert.False(element.Contours[0].Closed);
    }

    [Fact]
    public void RgbColor_IsParsed()
    {
        var shape = SvgDocumentParser.Parse(
            "<svg viewBox='0 0 10 10' fill='rgb(255, 128, 0)'><rect width='10' height='10'/></svg>");

        var element = Assert.Single(shape.Elements);
        Assert.Equal(SvgPaintKind.Literal, element.Fill.Kind);
        Assert.Equal(new ColorF(1f, 128f / 255f, 0f, 1f), element.Fill.Color);
    }
}
