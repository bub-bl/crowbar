using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// Parses an SVG document into a <see cref="SvgShape"/> for
/// <see cref="Renderer2D.DrawSvg"/>. Shape elements (<c>path</c>,
/// <c>circle</c>, <c>rect</c>, <c>ellipse</c>, <c>line</c>, <c>polyline</c>,
/// <c>polygon</c>) are converted to flattened contours; <c>g</c> groups carry
/// the inherited fill/stroke/stroke-width. <c>currentColor</c> is resolved to
/// the caller's tint at draw time, not here. This runs once per asset.
/// </summary>
public static class SvgDocumentParser
{
    public static SvgShape Parse(string svg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(svg);
        var document = XDocument.Parse(svg, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new FormatException("SVG document has no root element.");

        var shape = SvgShape.Create(ParseViewBox(root));

        var state = PaintState.Root;
        state.Fill = ResolvePaint(state.Fill, root.Attribute("fill")?.Value);
        state.Stroke = ResolvePaint(state.Stroke, root.Attribute("stroke")?.Value);
        state.StrokeWidth = InheritFloat(state.StrokeWidth, root.Attribute("stroke-width")?.Value);

        foreach (var child in root.Elements())
            Walk(child, state, shape.Elements);

        return shape;
    }

    private struct PaintState
    {
        public static readonly PaintState Root = new()
        {
            Fill = SvgPaint.Tint,     // SVG default fill is black; icons pass a tint.
            Stroke = SvgPaint.None,
            StrokeWidth = 0f,
            FillRule = SvgFillRule.NonZero,
            Opacity = 1f
        };

        public SvgPaint Fill;
        public SvgPaint Stroke;
        public float StrokeWidth;
        public SvgFillRule FillRule;
        public float Opacity;

        public PaintState(SvgPaint fill, SvgPaint stroke, float strokeWidth, SvgFillRule fillRule, float opacity)
        {
            Fill = fill;
            Stroke = stroke;
            StrokeWidth = strokeWidth;
            FillRule = fillRule;
            Opacity = opacity;
        }
    }

    private static void Walk(XElement element, in PaintState inherited, List<SvgElement> elements)
    {
        var state = new PaintState(
            ResolvePaint(inherited.Fill, element.Attribute("fill")?.Value),
            ResolvePaint(inherited.Stroke, element.Attribute("stroke")?.Value),
            InheritFloat(inherited.StrokeWidth, element.Attribute("stroke-width")?.Value),
            ResolveFillRule(inherited.FillRule, element.Attribute("fill-rule")?.Value),
            InheritFloat(inherited.Opacity, element.Attribute("opacity")?.Value));

        switch (element.Name.LocalName.ToLowerInvariant())
        {
            case "path":
            {
                var data = element.Attribute("d")?.Value;
                if (!string.IsNullOrWhiteSpace(data))
                    elements.Add(MakeElement(SvgFlattener.Flatten(SvgPathParser.Parse(data)), state));
                break;
            }
            case "circle":
                elements.Add(MakeElement(Circle(element), state));
                break;
            case "rect":
                elements.Add(MakeElement(Rect(element), state));
                break;
            case "ellipse":
                elements.Add(MakeElement(Ellipse(element), state));
                break;
            case "line":
                elements.Add(MakeElement(Line(element, closed: false), state));
                break;
            case "polyline":
                elements.Add(MakeElement(Poly(element, closed: false), state));
                break;
            case "polygon":
                elements.Add(MakeElement(Poly(element, closed: true), state));
                break;
        }

        foreach (var child in element.Elements())
            Walk(child, state, elements);
    }

    private static SvgElement MakeElement(List<SvgContour> contours, in PaintState state) => new()
    {
        Contours = contours,
        Fill = state.Fill,
        Stroke = state.Stroke,
        StrokeWidth = state.StrokeWidth,
        FillRule = state.FillRule,
        Opacity = state.Opacity
    };

    private static SvgFillRule ResolveFillRule(SvgFillRule inherited, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return inherited;
        return value.Trim().Equals("evenodd", StringComparison.OrdinalIgnoreCase)
            ? SvgFillRule.EvenOdd
            : SvgFillRule.NonZero;
    }

    private static List<SvgContour> Circle(XElement element)
    {
        var cx = ParseFloat(element.Attribute("cx")?.Value) ?? 0f;
        var cy = ParseFloat(element.Attribute("cy")?.Value) ?? 0f;
        var r = ParseFloat(element.Attribute("r")?.Value) ?? 0f;
        var contour = new SvgContour { Closed = true };
        SampleEllipse(contour.Points, cx, cy, r, r);
        return [contour];
    }

    private static List<SvgContour> Ellipse(XElement element)
    {
        var cx = ParseFloat(element.Attribute("cx")?.Value) ?? 0f;
        var cy = ParseFloat(element.Attribute("cy")?.Value) ?? 0f;
        var rx = ParseFloat(element.Attribute("rx")?.Value) ?? 0f;
        var ry = ParseFloat(element.Attribute("ry")?.Value) ?? 0f;
        var contour = new SvgContour { Closed = true };
        SampleEllipse(contour.Points, cx, cy, rx, ry);
        return [contour];
    }

    private static List<SvgContour> Rect(XElement element)
    {
        var x = ParseFloat(element.Attribute("x")?.Value) ?? 0f;
        var y = ParseFloat(element.Attribute("y")?.Value) ?? 0f;
        var width = ParseFloat(element.Attribute("width")?.Value) ?? 0f;
        var height = ParseFloat(element.Attribute("height")?.Value) ?? 0f;
        var rx = ParseFloat(element.Attribute("rx")?.Value) ?? 0f;
        var ry = ParseFloat(element.Attribute("ry")?.Value) ?? rx;
        rx = MathF.Min(rx, width * 0.5f);
        ry = MathF.Min(ry, height * 0.5f);

        var contour = new SvgContour { Closed = true };
        var points = contour.Points;
        if (rx <= 0f || ry <= 0f)
        {
            points.Add(new Vector2(x, y));
            points.Add(new Vector2(x + width, y));
            points.Add(new Vector2(x + width, y + height));
            points.Add(new Vector2(x, y + height));
        }
        else
        {
            // Clockwise: straight edges interleaved with quarter-corner arcs.
            points.Add(new Vector2(x + rx, y));
            points.Add(new Vector2(x + width - rx, y));
            SampleArc(points, new Vector2(x + width - rx, y + ry), rx, ry, -MathF.PI / 2f, 0f);
            points.Add(new Vector2(x + width, y + height - ry));
            SampleArc(points, new Vector2(x + width - rx, y + height - ry), rx, ry, 0f, MathF.PI / 2f);
            points.Add(new Vector2(x + rx, y + height));
            SampleArc(points, new Vector2(x + rx, y + height - ry), rx, ry, MathF.PI / 2f, MathF.PI);
            points.Add(new Vector2(x, y + ry));
            SampleArc(points, new Vector2(x + rx, y + ry), rx, ry, MathF.PI, 3f * MathF.PI / 2f);
        }
        return [contour];
    }

    private static List<SvgContour> Line(XElement element, bool closed)
    {
        var contour = new SvgContour { Closed = closed };
        contour.Points.Add(new Vector2(ParseFloat(element.Attribute("x1")?.Value) ?? 0f, ParseFloat(element.Attribute("y1")?.Value) ?? 0f));
        contour.Points.Add(new Vector2(ParseFloat(element.Attribute("x2")?.Value) ?? 0f, ParseFloat(element.Attribute("y2")?.Value) ?? 0f));
        return [contour];
    }

    private static List<SvgContour> Poly(XElement element, bool closed)
    {
        var points = element.Attribute("points")?.Value;
        var contour = new SvgContour { Closed = closed };
        if (!string.IsNullOrWhiteSpace(points))
        {
            var numbers = ParseNumbers(points);
            for (var i = 0; i + 1 < numbers.Count; i += 2)
                contour.Points.Add(new Vector2(numbers[i], numbers[i + 1]));
        }
        return [contour];
    }

    /// <summary>Samples a full ellipse (or circle) into a closed contour.</summary>
    private static void SampleEllipse(List<Vector2> points, float cx, float cy, float rx, float ry)
    {
        if (rx <= 0f || ry <= 0f)
            return;
        const int segments = 64;
        for (var i = 0; i < segments; i++)
        {
            var angle = TwoPi * i / segments;
            points.Add(new Vector2(cx + rx * MathF.Cos(angle), cy + ry * MathF.Sin(angle)));
        }
    }

    /// <summary>Samples a quarter elliptical arc from <paramref name="a0"/> to <paramref name="a1"/>.</summary>
    private static void SampleArc(List<Vector2> points, Vector2 center, float rx, float ry, float a0, float a1)
    {
        const int segments = 16;
        for (var i = 1; i <= segments; i++)
        {
            var angle = a0 + (a1 - a0) * (i / (float)segments);
            points.Add(new Vector2(center.X + rx * MathF.Cos(angle), center.Y + ry * MathF.Sin(angle)));
        }
    }

    private static RectF ParseViewBox(XElement root)
    {
        var viewBox = root.Attribute("viewBox")?.Value;
        if (!string.IsNullOrWhiteSpace(viewBox))
        {
            var numbers = ParseNumbers(viewBox);
            if (numbers.Count >= 4 && numbers[2] > 0f && numbers[3] > 0f)
                return new RectF(numbers[0], numbers[1], numbers[2], numbers[3]);
        }
        var width = ParseFloat(root.Attribute("width")?.Value) ?? 24f;
        var height = ParseFloat(root.Attribute("height")?.Value) ?? 24f;
        return new RectF(0f, 0f, MathF.Max(width, 1f), MathF.Max(height, 1f));
    }

    private static SvgPaint ResolvePaint(SvgPaint inherited, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return inherited;
        value = value.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return SvgPaint.None;
        if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("inherit", StringComparison.OrdinalIgnoreCase))
            return SvgPaint.Tint;
        var color = ParseColor(value);
        return color is { } c ? SvgPaint.Literal(c) : SvgPaint.Tint;
    }

    private static float InheritFloat(float inherited, string? value) =>
        string.IsNullOrWhiteSpace(value) ? inherited : ParseFloat(value) ?? inherited;

    private static ColorF? ParseColor(string value)
    {
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            return hex.Length switch
            {
                3 => ColorF.FromRgba(H(hex[0], hex[0]), H(hex[1], hex[1]), H(hex[2], hex[2])),
                4 => ColorF.FromRgba(H(hex[0], hex[0]), H(hex[1], hex[1]), H(hex[2], hex[2]), H(hex[3], hex[3])),
                6 => ColorF.FromRgba(H(hex[0], hex[1]), H(hex[2], hex[3]), H(hex[4], hex[5])),
                8 => ColorF.FromRgba(H(hex[0], hex[1]), H(hex[2], hex[3]), H(hex[4], hex[5]), H(hex[6], hex[7])),
                _ => null
            };
        }
        if (value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var open = value.IndexOf('(');
            var close = value.IndexOf(')');
            if (open < 0 || close < open)
                return null;
            var parts = value[(open + 1)..close].Split(',', ' ');
            var channels = new List<byte>();
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part))
                    continue;
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    channels.Add((byte)Math.Clamp(number, 0, 255));
                else
                    return null;
            }
            return channels.Count switch
            {
                3 => ColorF.FromRgba(channels[0], channels[1], channels[2]),
                4 => ColorF.FromRgba(channels[0], channels[1], channels[2], channels[3]),
                _ => null
            };
        }
        return null;
    }

    private static byte H(char hi, char lo) => (byte)(HexValue(hi) * 16 + HexValue(lo));

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0
    };

    private static float? ParseFloat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        // Strip trailing unit suffixes (px, pt, em, %).
        var number = value.Trim();
        var end = number.Length;
        while (end > 0 && !char.IsDigit(number[end - 1]) && number[end - 1] != '.')
            end--;
        return float.TryParse(number[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static List<float> ParseNumbers(string value)
    {
        var numbers = new List<float>();
        foreach (var token in value.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                numbers.Add(number);
        }
        return numbers;
    }

    private const float TwoPi = 2f * MathF.PI;
}
