using System.Numerics;

namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// Rasterizes a parsed <see cref="SvgShape"/> into straight-alpha RGBA8 pixels,
/// used by the gizmo icon atlas. Fills only (the engine's gizmo icons carry no
/// strokes): each element's closed contours are filled under the SVG non-zero
/// or even-odd rule, composited in document order with per-element opacity, and
/// antialiased by supersampling. The tint resolves <c>currentColor</c>; literal
/// colors are used as-is.
/// </summary>
internal static class SvgRasterizer
{
    private const int Supersample = 4;

    public static byte[] Rasterize(SvgShape shape, int size, ColorF tint)
    {
        var pixels = new byte[size * size * 4];
        var viewBox = shape.ViewBox;
        if (viewBox.Width <= 0f || viewBox.Height <= 0f)
            return pixels;

        // Contain the viewBox in the target, centered (same fit the old Skia
        // rasterizer used).
        var scale = MathF.Min(size / viewBox.Width, size / viewBox.Height);
        var offsetX = (size - viewBox.Width * scale) * 0.5f;
        var offsetY = (size - viewBox.Height * scale) * 0.5f;

        // Transform every filled contour into pixel space once.
        var elements = new List<FilledElement>();
        foreach (var element in shape.Elements)
        {
            if (element.Fill.Kind == SvgPaintKind.None || element.Opacity <= 0f)
                continue;
            var contours = new List<List<Vector2>>();
            foreach (var contour in element.Contours)
            {
                if (contour.Points.Count < 3)
                    continue;
                var points = new List<Vector2>(contour.Points.Count);
                foreach (var point in contour.Points)
                    points.Add(new Vector2(
                        offsetX + (point.X - viewBox.X) * scale,
                        offsetY + (point.Y - viewBox.Y) * scale));
                contours.Add(points);
            }
            if (contours.Count == 0)
                continue;
            elements.Add(new FilledElement(
                contours,
                element.Fill.Kind == SvgPaintKind.Literal ? element.Fill.Color : tint,
                element.FillRule,
                element.Opacity));
        }

        if (elements.Count == 0)
            return pixels;

        var step = 1f / Supersample;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // Accumulate premultiplied RGBA across the subsamples so the
                // average composites correctly at the edges.
                float r = 0f, g = 0f, b = 0f, a = 0f;
                for (var sy = 0; sy < Supersample; sy++)
                {
                    for (var sx = 0; sx < Supersample; sx++)
                    {
                        var p = new Vector2(x + (sx + 0.5f) * step, y + (sy + 0.5f) * step);
                        float pr = 0f, pg = 0f, pb = 0f, pa = 0f;
                        foreach (var element in elements)
                        {
                            if (!element.Contains(p))
                                continue;
                            var sa = element.Opacity;
                            pr = element.Color.R * sa + pr * (1f - sa);
                            pg = element.Color.G * sa + pg * (1f - sa);
                            pb = element.Color.B * sa + pb * (1f - sa);
                            pa = sa + pa * (1f - sa);
                        }
                        r += pr;
                        g += pg;
                        b += pb;
                        a += pa;
                    }
                }

                var sampleCount = Supersample * Supersample;
                a /= sampleCount;
                if (a > 0f)
                {
                    // Un-premultiply for the straight-alpha atlas the sprite
                    // shader samples.
                    var index = (y * size + x) * 4;
                    pixels[index] = ToByte(r / (a * sampleCount));
                    pixels[index + 1] = ToByte(g / (a * sampleCount));
                    pixels[index + 2] = ToByte(b / (a * sampleCount));
                    pixels[index + 3] = ToByte(a);
                }
            }
        }

        return pixels;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    private readonly struct FilledElement
    {
        public FilledElement(List<List<Vector2>> contours, ColorF color, SvgFillRule fillRule, float opacity)
        {
            Contours = contours;
            Color = color;
            FillRule = fillRule;
            Opacity = opacity;
        }

        public List<List<Vector2>> Contours { get; }
        public ColorF Color { get; }
        public SvgFillRule FillRule { get; }
        public float Opacity { get; }

        public bool Contains(Vector2 p)
        {
            var winding = 0;
            foreach (var contour in Contours)
                winding += Winding(contour, p);
            return FillRule == SvgFillRule.EvenOdd ? (winding & 1) != 0 : winding != 0;
        }
    }

    /// <summary>Winding number of a point against a closed polygon (non-zero rule basis).</summary>
    private static int Winding(List<Vector2> points, Vector2 p)
    {
        var winding = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (a.Y <= p.Y)
            {
                if (b.Y > p.Y && IsLeft(a, b, p) > 0f) winding++;
            }
            else
            {
                if (b.Y <= p.Y && IsLeft(a, b, p) < 0f) winding--;
            }
        }
        return winding;
    }

    private static float IsLeft(Vector2 a, Vector2 b, Vector2 p) =>
        (b.X - a.X) * (p.Y - a.Y) - (p.X - a.X) * (b.Y - a.Y);
}
