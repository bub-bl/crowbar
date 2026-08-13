using System.Numerics;

namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// A parsed SVG ready to draw through <see cref="Renderer2D.DrawSvg"/>. Build
/// one with <see cref="SvgDocumentParser.Parse"/> and cache it — the parser and
/// curve flattener run once, and the renderer only walks the flattened
/// contours, so no XML or Bézier work happens per frame.
/// </summary>
public sealed class SvgShape
{
    /// <summary>The document coordinate space (viewBox or width/height).</summary>
    public RectF ViewBox { get; internal set; }

    internal List<SvgElement> Elements { get; } = [];

    internal static SvgShape Create(RectF viewBox) => new() { ViewBox = viewBox };
}

/// <summary>How a fill or stroke is painted: nothing, the caller's tint, or a literal color.</summary>
internal enum SvgPaintKind : byte
{
    None = 0,
    Tint = 1,
    Literal = 2
}

internal readonly record struct SvgPaint(SvgPaintKind Kind, ColorF Color)
{
    public static readonly SvgPaint None = new(SvgPaintKind.None, default);
    public static readonly SvgPaint Tint = new(SvgPaintKind.Tint, default);

    public static SvgPaint Literal(ColorF color) => new(SvgPaintKind.Literal, color);
}

/// <summary>How a fill is resolved: even-odd or non-zero winding.</summary>
internal enum SvgFillRule : byte
{
    NonZero = 0,
    EvenOdd = 1
}

/// <summary>One drawable element: a fill and/or stroke over a set of contours.</summary>
internal sealed class SvgElement
{
    public List<SvgContour> Contours { get; set; } = [];
    public SvgPaint Fill = SvgPaint.None;
    public SvgPaint Stroke = SvgPaint.None;
    public float StrokeWidth;
    public SvgFillRule FillRule = SvgFillRule.NonZero;
    public float Opacity = 1f;
}

/// <summary>A flattened outline in SVG (viewBox) coordinates, open or closed.</summary>
internal sealed class SvgContour
{
    public List<Vector2> Points { get; } = [];
    public bool Closed;
}
