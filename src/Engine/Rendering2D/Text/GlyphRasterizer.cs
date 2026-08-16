using System.Buffers;
using System.Numerics;

namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// Turns a glyph's flattened outline (a flat list of directed edges) into a
/// multi-channel signed distance field (MSDF) packed as an RGBA8
/// <see cref="Texture2D"/>. RGB hold the three channel distances and alpha
/// carries the true signed distance (MTSDF fallback);
/// the glyph shader reconstructs the distance as the median of the three
/// channels, which stays exact at sharp corners under bilinear filtering
/// (a single-channel SDF would round them). Edge coloring and the per-channel
/// distance semantics follow Chlumský's msdfgen.
/// </summary>
internal static class GlyphRasterizer
{
    /// <summary>Outside border (in pixels) added around the ink, so the field has room and no atlas bleeding occurs.</summary>
    public const int Padding = 3;

    /// <summary>
    /// Field supersampling factor. The distance field is rasterized at
    /// <see cref="Scale"/>× the display size and the quad is drawn at 1×, so
    /// thin strokes and curves get accurate distances instead of being lost
    /// between two texels. The shader's SPREAD value is the 1× range scaled by
    /// this factor; its antialiasing width is derived from the texture gradient.
    /// </summary>
    public const int Scale = 2;

    /// <summary>Distance range (grid units) the stored field captures: +spread inside, -spread outside.</summary>
    public const float Spread = 8f;

    // Edge colors are a bit mask of the channels the edge contributes to.
    private const int ColorRed = 0b001;
    private const int ColorGreen = 0b010;
    private const int ColorBlue = 0b100;
    private const int ColorWhite = 0b111;
    private const int ColorBlack = 0b000;

    // A turn counts as a corner when the two edge directions turn by >= 90°
    // or deviate from straight by more than this angle (msdfgen's isCorner,
    // default 30°). Corners are where the coloring must switch channels so the
    // median of three can reproduce them exactly.
    private const float CornerAngle = 30f * MathF.PI / 180f;

    // Fixed seed: coloring is deterministic, so an identical glyph always
    // rasterizes to an identical texture.
    private const ulong ColoringSeed = 0x6D2B79F5E1E2C3D4UL;

    // Deviation threshold (stored units) for the error-correction pass: texels
    // whose median deviates from the true distance by more than ~1.5 grid
    // units (0.75 px) are flattened to a single channel so bilinear
    // interpolation between texels stays artifact-free.
    private const float ErrorThreshold = 1.5f;

    // Texels within one texel of the true boundary carry the corner-preserving
    // multi-channel structure and are never flattened. This must be measured
    // from the true field, not the median: a line-extension artifact can itself
    // look like a boundary and would otherwise evade correction forever.
    private const float ProtectionRadius = 1.001f;

    private readonly struct Segment
    {
        public readonly Vector2 A;
        public readonly Vector2 B;
        public readonly int Color;

        public Segment(Vector2 a, Vector2 b, int color)
        {
            A = a;
            B = b;
            Color = color;
        }
    }

    private readonly struct RasterSegment
    {
        public readonly float Ax;
        public readonly float Ay;
        public readonly float Dx;
        public readonly float Dy;
        public readonly float InvLength;
        public readonly float InvLengthSquared;
        public readonly int Color;
        public readonly int X0;
        public readonly int X1;
        public readonly int Y0;
        public readonly int Y1;

        public RasterSegment(Vector2 a, Vector2 b, int color, Vector2 min, int gridWidth, int gridHeight, float range)
        {
            Ax = a.X;
            Ay = a.Y;
            Dx = b.X - a.X;
            Dy = b.Y - a.Y;
            var lengthSquared = Dx * Dx + Dy * Dy;
            InvLengthSquared = 1f / lengthSquared;
            InvLength = MathF.Sqrt(InvLengthSquared);
            Color = color;

            var padding = Padding * Scale;
            X0 = Math.Clamp((int)MathF.Floor((MathF.Min(a.X, b.X) - range - min.X) * Scale + padding - 0.5f), 0, gridWidth - 1);
            X1 = Math.Clamp((int)MathF.Ceiling((MathF.Max(a.X, b.X) + range - min.X) * Scale + padding - 0.5f) - 1, 0, gridWidth - 1);
            Y0 = Math.Clamp((int)MathF.Floor((MathF.Min(a.Y, b.Y) - range - min.Y) * Scale + padding - 0.5f), 0, gridHeight - 1);
            Y1 = Math.Clamp((int)MathF.Ceiling((MathF.Max(a.Y, b.Y) + range - min.Y) * Scale + padding - 0.5f) - 1, 0, gridHeight - 1);
        }
    }

    private const float Epsilon = 1e-12f;

    /// <summary>
    /// Rasterizes <paramref name="edges"/> (screen-space, row-major pairs) whose
    /// axis-aligned bounds start at <paramref name="min"/> with the given ink
    /// size, into an MSDF texture of (inkWidth + 2·Padding) × (inkHeight + 2·Padding)
    /// scaled by <see cref="Scale"/>. Each grid cell is 1/Scale screen pixels;
    /// the stored field covers ±(Spread·Scale) grid units (±Spread display px).
    /// </summary>
    public static Texture2D Rasterize(IReadOnlyList<Vector2> edges, Vector2 min, int inkWidth, int inkHeight)
    {
        var gridWidth = (inkWidth + Padding * 2) * Scale;
        var gridHeight = (inkHeight + Padding * 2) * Scale;
        var range = Spread * Scale;
        var displayRangeSquared = Spread * Spread;

        var seed = ColoringSeed;
        var segments = BuildColoredSegments(edges, ref seed);
        var rasterSegments = new RasterSegment[segments.Count];
        for (var i = 0; i < segments.Count; i++)
            rasterSegments[i] = new RasterSegment(segments[i].A, segments[i].B, segments[i].Color, min, gridWidth, gridHeight, Spread);

        var pixelCount = checked(gridWidth * gridHeight);
        // Six values per texel: nearest finite squared distance and pseudo
        // distance for each of the three channels. The old implementation
        // scanned every edge for every texel; this representation lets us
        // update only texels in each edge's range-expanded bounding box.
        var valueLength = checked(pixelCount * 6);
        var pixelLength = checked(pixelCount * 4);
        var values = ArrayPool<float>.Shared.Rent(valueLength);
        var inside = ArrayPool<byte>.Shared.Rent(pixelCount);
        var pixels = ArrayPool<byte>.Shared.Rent(pixelLength);
        var trueStored = ArrayPool<float>.Shared.Rent(pixelCount);
        try
        {
        for (var i = 0; i < pixelCount; i++)
        {
            values[i * 6] = displayRangeSquared;
            values[i * 6 + 1] = displayRangeSquared;
            values[i * 6 + 2] = displayRangeSquared;
            values[i * 6 + 3] = Spread;
            values[i * 6 + 4] = Spread;
            values[i * 6 + 5] = Spread;
        }

        var padding = Padding * Scale;
        for (var segmentIndex = 0; segmentIndex < rasterSegments.Length; segmentIndex++)
        {
            var segment = rasterSegments[segmentIndex];
            if (segment.X0 > segment.X1 || segment.Y0 > segment.Y1)
                continue;

            for (var y = segment.Y0; y <= segment.Y1; y++)
            {
                var py = min.Y + (y - padding + 0.5f) / Scale;
                for (var x = segment.X0; x <= segment.X1; x++)
                {
                    var px = min.X + (x - padding + 0.5f) / Scale;
                    var qx = px - segment.Ax;
                    var qy = py - segment.Ay;
                    var projection = (qx * segment.Dx + qy * segment.Dy) * segment.InvLengthSquared;
                    var closestX = projection <= 0f
                        ? segment.Ax
                        : projection >= 1f
                            ? segment.Ax + segment.Dx
                            : segment.Ax + segment.Dx * projection;
                    var closestY = projection <= 0f
                        ? segment.Ay
                        : projection >= 1f
                            ? segment.Ay + segment.Dy
                            : segment.Ay + segment.Dy * projection;
                    var ex = px - closestX;
                    var ey = py - closestY;
                    var trueSquared = ex * ex + ey * ey;
                    if (trueSquared >= displayRangeSquared)
                        continue;

                    var cross = segment.Dx * qy - segment.Dy * qx;
                    var pseudo = MathF.Min(MathF.Abs(cross) * segment.InvLength, range);
                    var baseIndex = (y * gridWidth + x) * 6;
                    if ((segment.Color & ColorRed) != 0 && trueSquared < values[baseIndex])
                    {
                        values[baseIndex] = trueSquared;
                        values[baseIndex + 3] = pseudo;
                    }
                    if ((segment.Color & ColorGreen) != 0 && trueSquared < values[baseIndex + 1])
                    {
                        values[baseIndex + 1] = trueSquared;
                        values[baseIndex + 4] = pseudo;
                    }
                    if ((segment.Color & ColorBlue) != 0 && trueSquared < values[baseIndex + 2])
                    {
                        values[baseIndex + 2] = trueSquared;
                        values[baseIndex + 5] = pseudo;
                    }
                }
            }
        }

        // Build the even-odd sign mask with one scanline pass per row instead
        // of testing every texel against every edge.
        BuildInsideMask(edges, min, gridWidth, gridHeight, inside);
        for (var i = 0; i < pixelCount; i++)
        {
            var baseIndex = i * 6;
            var trueSquared = MathF.Min(values[baseIndex], MathF.Min(values[baseIndex + 1], values[baseIndex + 2]));
            var dTrue = MathF.Sqrt(trueSquared) * Scale;
            var d0 = values[baseIndex + 3] * Scale;
            var d1 = values[baseIndex + 4] * Scale;
            var d2 = values[baseIndex + 5] * Scale;
            var sign = inside[i] != 0 ? 1f : -1f;
            var s0 = ToStored(sign * d0, range);
            var s1 = ToStored(sign * d1, range);
            var s2 = ToStored(sign * d2, range);
            var trueValue = ToStored(sign * dTrue, range);
            var offset = i * 4;
            pixels[offset] = (byte)MathF.Round(s0 * 255f);
            pixels[offset + 1] = (byte)MathF.Round(s1 * 255f);
            pixels[offset + 2] = (byte)MathF.Round(s2 * 255f);
            pixels[offset + 3] = (byte)MathF.Round(trueValue * 255f);
            trueStored[i] = trueValue;
        }

        ApplyErrorCorrection(pixels, trueStored, gridWidth, gridHeight, range);
        return Texture2D.Create("glyph", gridWidth, gridHeight, pixels.AsSpan(0, pixelLength));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(values);
            ArrayPool<byte>.Shared.Return(inside);
            ArrayPool<byte>.Shared.Return(pixels);
            ArrayPool<float>.Shared.Return(trueStored);
        }
    }

    private static void BuildInsideMask(IReadOnlyList<Vector2> edges, Vector2 min, int gridWidth, int gridHeight, byte[] inside)
    {
        Array.Clear(inside, 0, checked(gridWidth * gridHeight));
        var intersections = new List<float>(edges.Count / 2);
        var padding = Padding * Scale;
        for (var y = 0; y < gridHeight; y++)
        {
            var py = min.Y + (y - padding + 0.5f) / Scale;
            intersections.Clear();
            for (var i = 0; i + 1 < edges.Count; i += 2)
            {
                var a = edges[i];
                var b = edges[i + 1];
                if ((a.Y > py) == (b.Y > py) || MathF.Abs(a.Y - b.Y) < Epsilon)
                    continue;
                intersections.Add(a.X + (py - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }
            intersections.Sort();
            for (var i = 0; i + 1 < intersections.Count; i += 2)
            {
                var left = intersections[i];
                var right = intersections[i + 1];
                if (left > right)
                    (left, right) = (right, left);
                var first = (int)MathF.Ceiling((left - min.X) * Scale + padding - 0.5f);
                var lastExclusive = (int)MathF.Ceiling((right - min.X) * Scale + padding - 0.5f);
                first = Math.Clamp(first, 0, gridWidth);
                lastExclusive = Math.Clamp(lastExclusive, 0, gridWidth);
                for (var x = first; x < lastExclusive; x++)
                    inside[y * gridWidth + x] = 1;
            }
        }
    }

    /// <summary>
    /// Simplified version of msdfgen's error correction. Texels whose median
    /// deviates from the true distance by more than <see cref="ErrorThreshold"/>
    /// grid units (dense regions where all three channels are near edges of
    /// different colors, so the median would land on the wrong one) are
    /// flattened to the true distance, keeping bilinear interpolation between
    /// texels artifact-free. Texels straddling the boundary are left untouched:
    /// they carry the multi-channel structure that preserves sharp corners.
    /// </summary>
    private static void ApplyErrorCorrection(byte[] pixels, float[] trueStored, int gridWidth, int gridHeight, float range)
    {
        var protection = ProtectionRadius / range;
        var threshold = ErrorThreshold / range;

        for (var y = 0; y < gridHeight; y++)
        for (var x = 0; x < gridWidth; x++)
        {
            var i = (y * gridWidth + x) * 4;
            var r = pixels[i] / 255f;
            var g = pixels[i + 1] / 255f;
            var b = pixels[i + 2] / 255f;
            var m = Median(r, g, b);
            var t = trueStored[y * gridWidth + x];
            if (MathF.Abs(t - 0.5f) > protection && MathF.Abs(m - t) > threshold)
            {
                var v = (byte)MathF.Round(t * 255f);
                pixels[i] = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
            }
        }
    }

    /// <summary>The median of three channels, as reconstructed by the glyph shader.</summary>
    private static float Median(float r, float g, float b) =>
        MathF.Max(MathF.Min(r, g), MathF.Min(MathF.Max(r, g), b));

    private static float ToStored(float distance, float range) =>
        Math.Clamp(0.5f + distance / range, 0f, 1f);

    /// <summary>
    /// Splits the flat edge list into contours (each figure is emitted as a
    /// closed run of consecutive edges that share their join vertex), colors
    /// every contour, and returns the colored segments.
    /// </summary>
    private static List<Segment> BuildColoredSegments(IReadOnlyList<Vector2> edges, ref ulong seed)
    {
        var segments = new List<Segment>(edges.Count / 2);
        var color = InitialColor(ref seed);
        var contour = new List<(Vector2 A, Vector2 B)>();
        for (var i = 0; i + 1 < edges.Count; i += 2)
        {
            var a = edges[i];
            var b = edges[i + 1];
            // A new contour begins when consecutive edges do not share their
            // join vertex (figures are flattened into the list contiguously).
            if (contour.Count > 0 && contour[^1].Item2 != a)
            {
                ColorContour(contour, ref color, ref seed, segments);
                contour = [];
            }
            contour.Add((a, b));
        }
        if (contour.Count > 0)
            ColorContour(contour, ref color, ref seed, segments);
        return segments;
    }

    /// <summary>
    /// Assigns each edge of <paramref name="contour"/> one of the three channel
    /// colors (or a multi-channel mask), ported from msdfgen's
    /// <c>edgeColoringSimple</c>: edges keep their color along smooth runs and
    /// switch at corners, so the two edges meeting at a sharp corner always
    /// have different colors (the median then reproduces the corner exactly).
    /// </summary>
    private static void ColorContour(List<(Vector2 A, Vector2 B)> contour, ref int color, ref ulong seed, List<Segment> output)
    {
        var n = contour.Count;
        var colors = new int[n];

        // Identify corners: a corner sits before edge i when the direction of
        // edge i-1 and edge i form a sharp turn.
        var corners = new List<int>();
        for (var i = 0; i < n; i++)
        {
            var prev = contour[(i + n - 1) % n];
            var cur = contour[i];
            if (IsCorner(prev.B - prev.A, cur.B - cur.A))
                corners.Add(i);
        }

        if (corners.Count == 0)
        {
            // Smooth contour: one color for the whole loop.
            SwitchColor(ref color, ref seed, ColorBlack);
            for (var i = 0; i < n; i++)
                colors[i] = color;
        }
        else if (corners.Count == 1)
        {
            // "Teardrop": a single sharp corner on an otherwise smooth contour.
            // The contour is split into three color runs; the middle run is
            // WHITE so every channel contributes near the corner.
            SwitchColor(ref color, ref seed, ColorBlack);
            var c0 = color;
            var c2 = color;
            SwitchColor(ref c2, ref seed, ColorBlack);
            var corner = corners[0];
            for (var i = 0; i < n; i++)
            {
                var t = SymmetricalTrichotomy(i, n);
                colors[(corner + i) % n] = t < 0 ? c0 : t > 0 ? c2 : ColorWhite;
            }
        }
        else
        {
            // Multiple corners: walk the contour from the first corner, keeping
            // the color between corners and switching at each one; the last
            // corner also avoids the initial color so the closed loop ends on a
            // different color than it started.
            var spline = 0;
            var start = corners[0];
            SwitchColor(ref color, ref seed, ColorBlack);
            var initialColor = color;
            for (var i = 0; i < n; i++)
            {
                var index = (start + i) % n;
                if (spline + 1 < corners.Count && corners[spline + 1] == index)
                {
                    spline++;
                    SwitchColor(ref color, ref seed, spline == corners.Count - 1 ? initialColor : ColorBlack);
                }
                colors[index] = color;
            }
        }

        for (var i = 0; i < n; i++)
            output.Add(new Segment(contour[i].A, contour[i].B, colors[i]));
    }

    /// <summary>
    /// Balanced trichotomy of a position over a loop: roughly the first third
    /// maps to -1, the middle third to 0 and the last third to +1.
    /// </summary>
    private static int SymmetricalTrichotomy(int position, int n) =>
        (int)(3 + 2.875 * position / (n - 1) - 1.4375 + 0.5) - 3;

    /// <summary>msdfgen's <c>isCorner</c>: a sharp turn between two normalized directions.</summary>
    private static bool IsCorner(Vector2 prevDir, Vector2 curDir)
    {
        var prevLen = prevDir.LengthSquared();
        var curLen = curDir.LengthSquared();
        if (prevLen < 1e-12f || curLen < 1e-12f)
            return false;
        var a = prevDir / MathF.Sqrt(prevLen);
        var b = curDir / MathF.Sqrt(curLen);
        var crossThreshold = MathF.Sin(CornerAngle);
        return Vector2.Dot(a, b) <= 0f || MathF.Abs(a.X * b.Y - a.Y * b.X) > crossThreshold;
    }

    private static void SwitchColor(ref int color, ref ulong seed, int banned)
    {
        // If the color and the banned color share exactly one channel, switch
        // to the other two; otherwise pick a different color at random.
        var combined = color & banned;
        if (combined == ColorRed || combined == ColorGreen || combined == ColorBlue)
            color = combined ^ ColorWhite;
        else
            color = NextColor(color, ref seed);
    }

    /// <summary>Rotates the three channel bits by one or two positions (seeded).</summary>
    private static int NextColor(int color, ref ulong seed)
    {
        var shift = 1 + (int)(seed & 1);
        seed >>= 1;
        var shifted = color << shift;
        return (shifted | shifted >> 3) & ColorWhite;
    }

    /// <summary>msdfgen starts from a two-channel color picked by the seed.</summary>
    private static int InitialColor(ref ulong seed)
    {
        var index = (int)(seed % 3);
        seed /= 3;
        return index switch
        {
            0 => ColorGreen | ColorBlue,
            1 => ColorRed | ColorBlue,
            _ => ColorRed | ColorGreen,
        };
    }

}
