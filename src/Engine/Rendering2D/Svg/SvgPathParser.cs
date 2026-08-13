using System.Globalization;
using System.Numerics;

namespace Crowbar.Engine.Rendering2D;

internal enum SvgSegmentKind : byte
{
    Move = 0,
    Line = 1,
    Cubic = 2,
    Quadratic = 3,
    Arc = 4,
    Close = 5
}

/// <summary>
/// One SVG path command. <c>P</c> is the target point (or the move target);
/// for cubic <c>P1</c>/<c>P2</c> are the two control points; for quadratic
/// <c>P1</c> is the control; for arcs <c>P1</c> holds the radii (x, y) and
/// <c>P2</c> holds the x-axis rotation in degrees.
/// </summary>
internal readonly struct SvgSegment
{
    public readonly SvgSegmentKind Kind;
    public readonly Vector2 P;
    public readonly Vector2 P1;
    public readonly Vector2 P2;
    public readonly bool LargeArc;
    public readonly bool Sweep;

    public SvgSegment(SvgSegmentKind kind, Vector2 p, Vector2 p1 = default, Vector2 p2 = default, bool largeArc = false, bool sweep = false)
    {
        Kind = kind;
        P = p;
        P1 = p1;
        P2 = p2;
        LargeArc = largeArc;
        Sweep = sweep;
    }

    public static SvgSegment Move(Vector2 p) => new(SvgSegmentKind.Move, p);
    public static SvgSegment Line(Vector2 p) => new(SvgSegmentKind.Line, p);
    public static SvgSegment Cubic(Vector2 c1, Vector2 c2, Vector2 p) => new(SvgSegmentKind.Cubic, p, c1, c2);
    public static SvgSegment Quad(Vector2 c, Vector2 p) => new(SvgSegmentKind.Quadratic, p, c);
    public static SvgSegment Arc(float rx, float ry, float rotation, bool largeArc, bool sweep, Vector2 p) =>
        new(SvgSegmentKind.Arc, p, new Vector2(rx, ry), new Vector2(rotation, 0f), largeArc, sweep);
    public static SvgSegment Close => new(SvgSegmentKind.Close, default);
}

/// <summary>
/// Parses an SVG <c>d</c> attribute into a flat <see cref="SvgSegment"/> list.
/// Supports <c>M/m L/l H/h V/v C/c S/s Q/q T/t A/a Z/z</c> with implicit
/// repetition, relative coordinates, and standard number syntax (signs,
/// exponents, leading/trailing decimal points). Runs on the CPU at load time;
/// it is never called during rendering.
/// </summary>
internal static class SvgPathParser
{
    public static List<SvgSegment> Parse(string data)
    {
        var segments = new List<SvgSegment>();
        if (string.IsNullOrWhiteSpace(data))
            return segments;

        var current = Vector2.Zero; // current point
        var start = Vector2.Zero;   // subpath start (for Z and S/T reflection reset)
        var lastCubicCtrl = Vector2.Zero;
        var lastQuadCtrl = Vector2.Zero;
        var cmd = '\0';
        var relative = false;
        var i = 0;

        while (true)
        {
            SkipSeparators(data, ref i);
            if (i >= data.Length)
                break;

            var ch = data[i];
            if (char.IsLetter(ch))
            {
                cmd = char.ToUpperInvariant(ch);
                relative = char.IsLower(ch);
                i++;
                SkipSeparators(data, ref i);
            }
            else if (cmd == '\0')
            {
                throw new FormatException($"SVG path: unexpected character '{ch}' at index {i}.");
            }

            switch (cmd)
            {
                case 'M':
                {
                    var p = ReadPoint(data, ref i);
                    var target = relative ? current + p : p;
                    segments.Add(SvgSegment.Move(target));
                    current = target;
                    start = target;
                    // Per the SVG spec, extra coordinate pairs after a moveto
                    // are implicit lineto commands.
                    cmd = 'L';
                    break;
                }
                case 'L':
                {
                    var p = ReadPoint(data, ref i);
                    current = relative ? current + p : p;
                    segments.Add(SvgSegment.Line(current));
                    break;
                }
                case 'H':
                {
                    var x = ReadNumber(data, ref i);
                    current = new Vector2(relative ? current.X + x : x, current.Y);
                    segments.Add(SvgSegment.Line(current));
                    break;
                }
                case 'V':
                {
                    var y = ReadNumber(data, ref i);
                    current = new Vector2(current.X, relative ? current.Y + y : y);
                    segments.Add(SvgSegment.Line(current));
                    break;
                }
                case 'C':
                {
                    var c1 = ReadPoint(data, ref i);
                    var c2 = ReadPoint(data, ref i);
                    var p = ReadPoint(data, ref i);
                    var a1 = relative ? current + c1 : c1;
                    var a2 = relative ? current + c2 : c2;
                    var end = relative ? current + p : p;
                    segments.Add(SvgSegment.Cubic(a1, a2, end));
                    current = end;
                    lastCubicCtrl = a2;
                    break;
                }
                case 'S':
                {
                    var c2 = ReadPoint(data, ref i);
                    var p = ReadPoint(data, ref i);
                    var prevCubic = segments.Count > 0 && segments[^1].Kind == SvgSegmentKind.Cubic;
                    var c1 = prevCubic ? Reflect(current, lastCubicCtrl) : current;
                    var a2 = relative ? current + c2 : c2;
                    var end = relative ? current + p : p;
                    segments.Add(SvgSegment.Cubic(c1, a2, end));
                    current = end;
                    lastCubicCtrl = a2;
                    break;
                }
                case 'Q':
                {
                    var c = ReadPoint(data, ref i);
                    var p = ReadPoint(data, ref i);
                    var a1 = relative ? current + c : c;
                    var end = relative ? current + p : p;
                    segments.Add(SvgSegment.Quad(a1, end));
                    current = end;
                    lastQuadCtrl = a1;
                    break;
                }
                case 'T':
                {
                    var p = ReadPoint(data, ref i);
                    var prevQuad = segments.Count > 0 && segments[^1].Kind == SvgSegmentKind.Quadratic;
                    var c = prevQuad ? Reflect(current, lastQuadCtrl) : current;
                    var end = relative ? current + p : p;
                    segments.Add(SvgSegment.Quad(c, end));
                    current = end;
                    lastQuadCtrl = c;
                    break;
                }
                case 'A':
                {
                    var rx = ReadNumber(data, ref i);
                    var ry = ReadNumber(data, ref i);
                    var rotation = ReadNumber(data, ref i);
                    var largeArc = ReadFlag(data, ref i);
                    var sweep = ReadFlag(data, ref i);
                    var p = ReadPoint(data, ref i);
                    var end = relative ? current + p : p;
                    segments.Add(SvgSegment.Arc(rx, ry, rotation, largeArc, sweep, end));
                    current = end;
                    break;
                }
                case 'Z':
                    segments.Add(SvgSegment.Close);
                    current = start;
                    break;
                default:
                    throw new FormatException($"SVG path: unsupported command '{cmd}'.");
            }
        }

        return segments;
    }

    private static Vector2 Reflect(Vector2 point, Vector2 control) => point * 2f - control;

    private static bool ReadFlag(string s, ref int i)
    {
        SkipSeparators(s, ref i);
        if (i >= s.Length)
            throw new FormatException("SVG path: expected arc flag.");
        var c = s[i];
        if (c == '0') { i++; return false; }
        if (c == '1') { i++; return true; }
        throw new FormatException($"SVG path: invalid arc flag '{c}'.");
    }

    private static Vector2 ReadPoint(string s, ref int i) =>
        new(ReadNumber(s, ref i), ReadNumber(s, ref i));

    private static float ReadNumber(string s, ref int i)
    {
        SkipSeparators(s, ref i);
        if (i >= s.Length)
            throw new FormatException("SVG path: expected number.");

        var start = i;
        var len = s.Length;
        if (i < len && (s[i] == '+' || s[i] == '-'))
            i++;
        while (i < len && char.IsDigit(s[i]))
            i++;
        if (i < len && s[i] == '.')
        {
            i++;
            while (i < len && char.IsDigit(s[i]))
                i++;
        }
        if (i < len && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < len && (s[i] == '+' || s[i] == '-'))
                i++;
            while (i < len && char.IsDigit(s[i]))
                i++;
        }

        var token = s[start..i];
        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"SVG path: invalid number '{token}'.");
        return value;
    }

    private static void SkipSeparators(string s, ref int i)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ','))
            i++;
    }
}
