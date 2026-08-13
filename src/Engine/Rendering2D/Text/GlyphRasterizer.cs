using System.Numerics;

namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// Turns a glyph's flattened outline (a flat list of directed edges) into a
/// single-channel signed distance field packed as an RGBA8 <see cref="Texture2D"/>
/// (distance replicated in RGB, alpha 255). The edge boundary sits at distance
/// 0.5 in the stored byte; the glyph shader reconstructs the distance and
/// smoothsteps it for antialiasing.
/// </summary>
internal static class GlyphRasterizer
{
    /// <summary>Outside border (in pixels) added around the ink, so the SDF has room and no atlas bleeding occurs.</summary>
    public const int Padding = 3;

    /// <summary>Distance range (pixels) the stored field captures: +spread inside, -spread outside.</summary>
    public const float Spread = 8f;

    /// <summary>
    /// Rasterizes <paramref name="edges"/> (screen-space, row-major pairs) whose
    /// axis-aligned bounds start at <paramref name="min"/> with the given ink
    /// size, into an SDF texture of (inkWidth + 2·Padding) × (inkHeight + 2·Padding).
    /// </summary>
    public static Texture2D Rasterize(IReadOnlyList<Vector2> edges, Vector2 min, int inkWidth, int inkHeight)
    {
        var gridWidth = inkWidth + Padding * 2;
        var gridHeight = inkHeight + Padding * 2;

        var inside = new bool[gridWidth * gridHeight];
        for (var y = 0; y < gridHeight; y++)
        for (var x = 0; x < gridWidth; x++)
        {
            var p = new Vector2(min.X + (x - Padding) + 0.5f, min.Y + (y - Padding) + 0.5f);
            inside[y * gridWidth + x] = Contains(p, edges);
        }

        var signed = DistanceField.Signed(inside, gridWidth, gridHeight);
        var pixels = new byte[gridWidth * gridHeight * 4];
        for (var i = 0; i < signed.Length; i++)
        {
            var s = Math.Clamp(0.5f + signed[i] / Spread, 0f, 1f);
            var value = (byte)MathF.Round(s * 255f);
            var offset = i * 4;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }

        return Texture2D.Create("glyph", gridWidth, gridHeight, pixels);
    }

    /// <summary>Even-odd point-in-polygon over the flattened edges (handles glyph holes).</summary>
    private static bool Contains(Vector2 p, IReadOnlyList<Vector2> edges)
    {
        var inside = false;
        for (var i = 0; i + 1 < edges.Count; i += 2)
        {
            var a = edges[i];
            var b = edges[i + 1];
            if ((a.Y > p.Y) != (b.Y > p.Y))
            {
                var x = a.X + (p.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                if (p.X < x)
                    inside = !inside;
            }
        }
        return inside;
    }
}
