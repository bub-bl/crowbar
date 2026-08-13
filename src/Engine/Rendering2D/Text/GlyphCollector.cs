using System.Numerics;
using SixLabors.Fonts;
using SixLabors.Fonts.Rendering;

namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// Bridges <see cref="TextRenderer.RenderTo"/> to <see cref="Renderer2D"/>:
/// it flattens each glyph's outline into directed edges and hands them to the
/// renderer, which rasterizes them into the glyph atlas and emits a textured
/// quad. Reused across draws, so repeated text allocates nothing.
/// </summary>
internal sealed class GlyphCollector : IGlyphRenderer
{
    private const float Flatness = 0.25f;

    private readonly Renderer2D _owner;
    private readonly List<Vector2> _edges = [];
    private ColorF _color;
    private string _fontKey = "";
    private int _sizeKey;
    private ushort _glyphId;
    private Vector2 _figureStart;
    private Vector2 _figureLast;
    private bool _hasFigure;

    public GlyphCollector(Renderer2D owner) => _owner = owner;

    /// <summary>Sets the per-draw state used for cache keys and quad emission.</summary>
    public void Configure(ColorF color, string fontKey, int sizeKey)
    {
        _color = color;
        _fontKey = fontKey;
        _sizeKey = sizeKey;
    }

    public void BeginText(in FontRectangle bounds)
    {
    }

    public void EndText()
    {
    }

    public bool BeginGlyph(in FontRectangle bounds, in GlyphRendererParameters parameters)
    {
        _edges.Clear();
        _glyphId = parameters.GlyphId;
        _hasFigure = false;
        return true;
    }

    public void EndGlyph()
    {
        if (_edges.Count >= 6)
            _owner.EmitGlyph(_edges, _color, _fontKey, _sizeKey, _glyphId);
        _edges.Clear();
    }

    public void BeginLayer(Paint? paint, FillRule fillRule)
    {
    }

    public void EndLayer()
    {
    }

    public void BeginGroup(CompositeMode mode)
    {
    }

    public void EndGroup()
    {
    }

    public void BeginFigure()
    {
        _hasFigure = false;
    }

    public void MoveTo(Vector2 point)
    {
        _figureStart = point;
        _figureLast = point;
        _hasFigure = true;
    }

    public void LineTo(Vector2 point)
    {
        AddEdge(_figureLast, point);
        _figureLast = point;
    }

    public void QuadraticBezierTo(Vector2 secondControlPoint, Vector2 point)
    {
        FlattenQuadratic(_figureLast, secondControlPoint, point);
        _figureLast = point;
    }

    public void CubicBezierTo(Vector2 secondControlPoint, Vector2 thirdControlPoint, Vector2 point)
    {
        FlattenCubic(_figureLast, secondControlPoint, thirdControlPoint, point);
        _figureLast = point;
    }

    public void ArcTo(float radiusX, float radiusY, float rotation, bool largeArc, bool sweep, Vector2 point)
    {
        // TrueType outlines are quadratic beziers; arcs are rare in glyphs. The
        // SDF smooths the error from this line approximation.
        LineTo(point);
    }

    public void EndFigure()
    {
        if (_hasFigure && _figureLast != _figureStart)
            AddEdge(_figureLast, _figureStart);
        _hasFigure = false;
    }

    public TextDecorations EnabledDecorations() => TextDecorations.None;

    public void SetDecoration(TextDecorations textDecorations, Vector2 start, Vector2 end, float thickness, ReadOnlyMemory<float> dashPattern)
    {
    }

    private void AddEdge(Vector2 a, Vector2 b)
    {
        if (a == b)
            return;
        _edges.Add(a);
        _edges.Add(b);
    }

    private void FlattenQuadratic(Vector2 p0, Vector2 c, Vector2 p1)
    {
        if (DistanceToLine(c, p0, p1) <= Flatness)
        {
            AddEdge(p0, p1);
            return;
        }
        var p01 = (p0 + c) * 0.5f;
        var p12 = (c + p1) * 0.5f;
        var p012 = (p01 + p12) * 0.5f;
        FlattenQuadratic(p0, p01, p012);
        FlattenQuadratic(p012, p12, p1);
    }

    private void FlattenCubic(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1)
    {
        if (DistanceToLine(c1, p0, p1) <= Flatness && DistanceToLine(c2, p0, p1) <= Flatness)
        {
            AddEdge(p0, p1);
            return;
        }
        var p01 = (p0 + c1) * 0.5f;
        var p12 = (c1 + c2) * 0.5f;
        var p23 = (c2 + p1) * 0.5f;
        var p012 = (p01 + p12) * 0.5f;
        var p123 = (p12 + p23) * 0.5f;
        var p0123 = (p012 + p123) * 0.5f;
        FlattenCubic(p0, p01, p012, p0123);
        FlattenCubic(p0123, p123, p23, p1);
    }

    private static float DistanceToLine(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared();
        if (lengthSquared < 1e-6f)
            return (p - a).Length();
        var t = Math.Clamp(Vector2.Dot(p - a, ab) / lengthSquared, 0f, 1f);
        return (p - (a + ab * t)).Length();
    }
}
