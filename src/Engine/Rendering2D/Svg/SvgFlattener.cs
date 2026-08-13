using System.Numerics;

namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// Flattens parsed <see cref="SvgSegment"/>s into polyline contours by
/// recursively subdividing cubic/quadratic Bézier curves (de Casteljau) and
/// sampling elliptical arcs, so the tessellator only ever sees straight edges.
/// Runs once at load time; the resulting contours are cached.
/// </summary>
internal static class SvgFlattener
{
    private const float TwoPi = 2f * MathF.PI;

    /// <summary>
    /// Flattens <paramref name="segments"/> into contours. <paramref name="tolerance"/>
    /// is the maximum allowed deviation (in SVG units) of a flattened curve
    /// from the true curve.
    /// </summary>
    public static List<SvgContour> Flatten(List<SvgSegment> segments, float tolerance = 0.25f)
    {
        var contours = new List<SvgContour>();
        var current = Vector2.Zero;
        SvgContour? contour = null;

        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case SvgSegmentKind.Move:
                    contour = new SvgContour();
                    contours.Add(contour);
                    contour.Points.Add(segment.P);
                    current = segment.P;
                    break;
                case SvgSegmentKind.Line:
                    contour!.Points.Add(segment.P);
                    current = segment.P;
                    break;
                case SvgSegmentKind.Cubic:
                    FlattenCubic(contour!.Points, current, segment.P1, segment.P2, segment.P, tolerance);
                    current = segment.P;
                    break;
                case SvgSegmentKind.Quadratic:
                    FlattenQuad(contour!.Points, current, segment.P1, segment.P, tolerance);
                    current = segment.P;
                    break;
                case SvgSegmentKind.Arc:
                    FlattenArc(contour!.Points, current, segment, tolerance);
                    current = segment.P;
                    break;
                case SvgSegmentKind.Close:
                    if (contour is not null)
                        contour.Closed = true;
                    break;
            }
        }

        return contours;
    }

    private static void FlattenCubic(List<Vector2> points, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float tolerance)
    {
        if (IsFlatCubic(p0, p1, p2, p3, tolerance))
        {
            points.Add(p3);
            return;
        }
        // de Casteljau split at t = 0.5.
        var p01 = (p0 + p1) * 0.5f;
        var p12 = (p1 + p2) * 0.5f;
        var p23 = (p2 + p3) * 0.5f;
        var p012 = (p01 + p12) * 0.5f;
        var p123 = (p12 + p23) * 0.5f;
        var p0123 = (p012 + p123) * 0.5f;
        FlattenCubic(points, p0, p01, p012, p0123, tolerance);
        FlattenCubic(points, p0123, p123, p23, p3, tolerance);
    }

    private static bool IsFlatCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float tolerance)
    {
        // Both control points must lie within `tolerance` of the chord p0-p3.
        var d1 = DistanceToSegment(p1, p0, p3);
        var d2 = DistanceToSegment(p2, p0, p3);
        return d1 <= tolerance && d2 <= tolerance;
    }

    private static void FlattenQuad(List<Vector2> points, Vector2 p0, Vector2 c, Vector2 p2, float tolerance)
    {
        if (DistanceToSegment(c, p0, p2) <= tolerance)
        {
            points.Add(p2);
            return;
        }
        var p01 = (p0 + c) * 0.5f;
        var p12 = (c + p2) * 0.5f;
        var mid = (p01 + p12) * 0.5f;
        FlattenQuad(points, p0, p01, mid, tolerance);
        FlattenQuad(points, mid, p12, p2, tolerance);
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
        if (lengthSquared <= 1e-12f)
            return Vector2.Distance(p, a);
        var t = Math.Clamp(Vector2.Dot(p - a, ab) / lengthSquared, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    private static void FlattenArc(List<Vector2> points, Vector2 from, SvgSegment segment, float tolerance)
    {
        var rx = MathF.Abs(segment.P1.X);
        var ry = MathF.Abs(segment.P1.Y);
        var rotation = segment.P2.X * (MathF.PI / 180f);
        var to = segment.P;

        // Degenerate arc (zero radius, or start == end): just a line.
        if (rx < 1e-6f || ry < 1e-6f || from == to)
        {
            points.Add(to);
            return;
        }

        var x1 = from.X;
        var y1 = from.Y;
        var x2 = to.X;
        var y2 = to.Y;
        var cosPhi = MathF.Cos(rotation);
        var sinPhi = MathF.Sin(rotation);

        // SVG F.6.5.1: the chord midpoint in the rotated frame.
        var dx = (x1 - x2) * 0.5f;
        var dy = (y1 - y2) * 0.5f;
        var x1p = cosPhi * dx + sinPhi * dy;
        var y1p = -sinPhi * dx + cosPhi * dy;

        // F.6.6: correct out-of-range radii.
        var lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1f)
        {
            var s = MathF.Sqrt(lambda);
            rx *= s;
            ry *= s;
        }

        // F.6.5.2: the ellipse center in the rotated frame.
        var rx2 = rx * rx;
        var ry2 = ry * ry;
        var numerator = rx2 * ry2 - rx2 * y1p * y1p - ry2 * x1p * x1p;
        var denominator = rx2 * y1p * y1p + ry2 * x1p * x1p;
        var sign = segment.LargeArc == segment.Sweep ? -1f : 1f;
        var coef = sign * MathF.Sqrt(MathF.Max(0f, numerator / denominator));
        var cxp = coef * (rx * y1p / ry);
        var cyp = coef * (-ry * x1p / rx);

        var cx = cosPhi * cxp - sinPhi * cyp + (x1 + x2) * 0.5f;
        var cy = sinPhi * cxp + cosPhi * cyp + (y1 + y2) * 0.5f;

        // F.6.5.3: start angle and sweep.
        var ux = (x1p - cxp) / rx;
        var uy = (y1p - cyp) / ry;
        var vx = (-x1p - cxp) / rx;
        var vy = (-y1p - cyp) / ry;
        var theta1 = MathF.Atan2(uy, ux);
        var delta = MathF.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        if (!segment.Sweep && delta > 0f)
            delta -= TwoPi;
        if (segment.Sweep && delta < 0f)
            delta += TwoPi;

        // Sample so the chord error stays under the tolerance.
        var maxRadius = MathF.Max(rx, ry);
        var maxStep = 2f * MathF.Sqrt(tolerance / MathF.Max(maxRadius, tolerance));
        var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(delta) / maxStep));
        for (var k = 1; k <= steps; k++)
        {
            var t = k / (float)steps;
            var angle = theta1 + delta * t;
            var cosA = MathF.Cos(angle);
            var sinA = MathF.Sin(angle);
            var x = cx + rx * cosPhi * cosA - ry * sinPhi * sinA;
            var y = cy + rx * sinPhi * cosA + ry * cosPhi * sinA;
            points.Add(new Vector2(x, y));
        }
    }
}
