using System.Numerics;

namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// Fills a set of closed contours under the SVG even-odd rule by sweeping a
/// horizontal scanline from top to bottom: active edges are sorted by x, paired
/// up, and each pair becomes a trapezoid split into two triangles. Because the
/// pairing is per-scanline, holes and self-intersections come out correctly
/// without a full polygon triangulator.
/// </summary>
internal static class EvenOddTriangulator
{
    private readonly struct Edge
    {
        public readonly Vector2 Top;
        public readonly Vector2 Bottom;

        public Edge(Vector2 top, Vector2 bottom)
        {
            Top = top;
            Bottom = bottom;
        }

        public float Y0 => Top.Y;
        public float Y1 => Bottom.Y;

        public float XAt(float y)
        {
            var t = (y - Top.Y) / (Bottom.Y - Top.Y);
            return Top.X + (Bottom.X - Top.X) * t;
        }
    }

    /// <summary>
    /// Triangulates <paramref name="contours"/> (treated as closed) and appends
    /// vertices three-at-a-time to <paramref name="triangles"/>.
    /// </summary>
    public static void Triangulate(List<List<Vector2>> contours, List<Vector2> triangles)
    {
        var edges = new List<Edge>();
        foreach (var contour in contours)
        {
            var count = contour.Count;
            if (count < 3)
                continue;
            for (var i = 0; i < count; i++)
            {
                var a = contour[i];
                var b = contour[(i + 1) % count];
                if (a.Y == b.Y)
                    continue; // horizontal edges never affect even-odd pairing
                edges.Add(a.Y < b.Y ? new Edge(a, b) : new Edge(b, a));
            }
        }
        if (edges.Count < 2)
            return;

        // Distinct scanlines at every vertex y, ascending.
        var scanlines = new List<float>(edges.Count * 2);
        foreach (var edge in edges)
        {
            scanlines.Add(edge.Y0);
            scanlines.Add(edge.Y1);
        }
        scanlines.Sort();

        edges.Sort(static (x, y) => x.Y0.CompareTo(y.Y0));

        var active = new List<int>();
        var edgeIndex = 0;
        var iScan = 0;
        while (iScan < scanlines.Count)
        {
            var yTop = scanlines[iScan];
            // Skip duplicated scanlines.
            while (iScan < scanlines.Count && scanlines[iScan] == yTop)
                iScan++;
            if (iScan >= scanlines.Count)
                break;
            var yBottom = scanlines[iScan];
            if (yBottom <= yTop)
                continue;

            // Drop edges that end at or above this band.
            active.RemoveAll(index => edges[index].Y1 <= yTop);

            // Insert edges that start on this scanline (and span the band).
            while (edgeIndex < edges.Count && edges[edgeIndex].Y0 <= yTop)
            {
                if (edges[edgeIndex].Y1 > yTop)
                    active.Add(edgeIndex);
                edgeIndex++;
            }

            if (active.Count < 2)
                continue;

            // Sort by x at the band's midpoint (edges cannot cross inside a band).
            var yMid = (yTop + yBottom) * 0.5f;
            active.Sort((a, b) => edges[a].XAt(yMid).CompareTo(edges[b].XAt(yMid)));

            for (var k = 0; k + 1 < active.Count; k += 2)
            {
                var left = edges[active[k]];
                var right = edges[active[k + 1]];
                EmitTrapezoid(
                    triangles,
                    left.XAt(yTop), right.XAt(yTop),
                    left.XAt(yBottom), right.XAt(yBottom),
                    yTop, yBottom);
            }
        }
    }

    private static void EmitTrapezoid(List<Vector2> triangles, float x0Top, float x1Top, float x0Bottom, float x1Bottom, float yTop, float yBottom)
    {
        var a = new Vector2(x0Top, yTop);
        var b = new Vector2(x1Top, yTop);
        var c = new Vector2(x1Bottom, yBottom);
        var d = new Vector2(x0Bottom, yBottom);

        // Split into two triangles, skipping degenerate (zero-area) ones.
        if (TriangleArea(a, b, c) > 1e-9f)
        {
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }
        if (TriangleArea(a, c, d) > 1e-9f)
        {
            triangles.Add(a);
            triangles.Add(c);
            triangles.Add(d);
        }
    }

    private static float TriangleArea(Vector2 a, Vector2 b, Vector2 c)
    {
        var cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        return MathF.Abs(cross) * 0.5f;
    }
}
