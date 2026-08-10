using System.Globalization;
using SkiaSharp;

namespace Crowbar.UI;

/// <summary>The kind of a single <see cref="TransformOp"/>.</summary>
public enum TransformOpType
{
    /// <summary>Full 2D affine matrix: <c>matrix(a, b, c, d, e, f)</c>.</summary>
    Matrix,
    /// <summary><c>translate(x, y)</c> — x/y may be percentages of the element box.</summary>
    Translate,
    TranslateX,
    TranslateY,
    Scale,
    ScaleX,
    ScaleY,
    Rotate,
    Skew,
    SkewX,
    SkewY
}

/// <summary>
/// A single CSS transform function with its numeric parameters. Parameters are
/// stored by meaning per type: translate (A=x, B=y), scale (A=sx, B=sy),
/// rotate (A=degrees), skew (A=x°, B=y°), matrix (A-F = a-f). The percent
/// flags mark translate components expressed as percentages.
/// </summary>
public readonly record struct TransformOp(
    TransformOpType Type,
    float A = 0, float B = 0, float C = 0, float D = 0, float E = 0, float F = 0,
    bool AIsPercent = false, bool BIsPercent = false);

/// <summary>
/// An ordered CSS <c>transform</c> value. Functions apply left-to-right like
/// CSS: the first function is applied to the element first. Rendering builds a
/// single affine matrix (with percentages resolved against the element box and
/// rotated/scaled around <see cref="TransformOrigin"/>); animation interpolates
/// function-by-function when both lists match, otherwise through 2D matrix
/// decomposition, following the CSS Transforms spec.
/// </summary>
public sealed class TransformList : IEquatable<TransformList>
{
    /// <summary>The identity transform (<c>transform: none</c>).</summary>
    public static readonly TransformList None = new([]);

    public IReadOnlyList<TransformOp> Ops { get; }

    public TransformList(IReadOnlyList<TransformOp> ops) => Ops = ops;

    /// <summary>True when the value is <c>none</c> (no transform applied).</summary>
    public bool IsNone => Ops.Count == 0;

    /// <summary>
    /// True when the composed matrix is the identity even though the list is not
    /// empty — e.g. <c>translate(0px, 0px)</c>, or an animation that ended at
    /// the identity. Such a list must not route through the transformed-paint
    /// path (local-space children + cull exemption): the result is identical
    /// and the identity short-circuit keeps damage culling in screen space.
    /// </summary>
    public bool IsIdentity
    {
        get
        {
            foreach (var op in Ops)
            {
                var m = OpMatrix(op, 1, 1);
                if (Math.Abs(m.ScaleX - 1) > 1e-4f || Math.Abs(m.SkewY) > 1e-4f || Math.Abs(m.SkewX) > 1e-4f ||
                    Math.Abs(m.ScaleY - 1) > 1e-4f || Math.Abs(m.TransX) > 1e-4f || Math.Abs(m.TransY) > 1e-4f) return false;
            }
            return true;
        }
    }

    public bool Equals(TransformList? other) => other is not null && Ops.SequenceEqual(other.Ops);
    public override bool Equals(object? obj) => Equals(obj as TransformList);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var op in Ops) hash.Add(op);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Builds the local→global affine matrix for an element of the given size at
    /// (<paramref name="offsetX"/>, <paramref name="offsetY"/>), with percentages
    /// resolved and rotation/scaling pivoting on <paramref name="origin"/>.
    /// </summary>
    public SKMatrix BuildMatrix(float width, float height, TransformOrigin origin, float offsetX, float offsetY)
    {
        var ox = origin.ResolveX(width);
        var oy = origin.ResolveY(height);
        // SkiaSharp's instance PostConcat(other) pre-multiplies (this = other ·
        // this), so each matrix must be introduced in the order it applies to a
        // point. Build the product as T(offset+origin) · ops · T(-origin): start
        // with the innermost shift (local point relative to the origin), apply
        // the functions in CSS order, then place the result at the box position.
        // The naive "T(offset+origin) first" order lands the back-shift before
        // the functions and pivots the transform around the NEGATED origin — a
        // scale then grows toward the bottom-right instead of around its center.
        var m = SKMatrix.CreateTranslation(-ox, -oy);
        foreach (var op in Ops) m = m.PostConcat(OpMatrix(op, width, height));
        m = m.PostConcat(SKMatrix.CreateTranslation(offsetX + ox, offsetY + oy));
        return m;
    }

    /// <summary>
    /// Parses a CSS <c>transform</c> value: <c>none</c> or a space-separated list
    /// of 2D functions (matrix, translate, translateX/Y, scale, scaleX/Y,
    /// rotate, skew, skewX/Y). Pure-3D functions (rotateX/Y/Z, translateZ,
    /// scaleZ, perspective, matrix3d) are accepted for compatibility: rotateZ is
    /// treated as a 2D rotation, the others apply no effect in the 2D renderer.
    /// </summary>
    public static bool TryParse(string value, out TransformList result)
    {
        result = None;
        var trimmed = value.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        var functions = CssValueParsers.SplitWhitespaceTokens(trimmed);
        if (functions.Length == 0) return false;
        var ops = new List<TransformOp>(functions.Length);
        foreach (var function in functions)
        {
            if (!TryParseFunction(function, out var op)) return false;
            if (op is not null) ops.Add(op.Value);
        }
        result = new TransformList(ops);
        return true;
    }

    /// <summary>
    /// Interpolates between two transforms. Matching function lists interpolate
    /// parameter by parameter; otherwise both are decomposed into
    /// translate/rotate/scale/skew, interpolated and recomposed (CSS matrix
    /// interpolation). Returns null when the values cannot be interpolated.
    /// </summary>
    public static TransformList? Lerp(TransformList? from, TransformList? to, float t)
    {
        if (from is null || to is null) return null;
        if (from.Ops.Count == to.Ops.Count && !from.Ops.Any(o => o.Type == TransformOpType.Matrix))
        {
            var lerped = new TransformOp[from.Ops.Count];
            for (var i = 0; i < from.Ops.Count; i++)
            {
                if (!TryLerpOp(from.Ops[i], to.Ops[i], t, out var op)) return LerpMatrices(from, to, t);
                lerped[i] = op;
            }
            return new TransformList(lerped);
        }
        return LerpMatrices(from, to, t);
    }

    /// <summary>Matrix of a single operation, with percentages resolved against the element box.</summary>
    private static SKMatrix OpMatrix(TransformOp op, float width, float height) => op.Type switch
    {
        TransformOpType.Matrix => new SKMatrix
        {
            ScaleX = op.A, SkewY = op.B, SkewX = op.C, ScaleY = op.D,
            TransX = op.E, TransY = op.F, Persp0 = 0, Persp1 = 0, Persp2 = 1
        },
        TransformOpType.Translate => SKMatrix.CreateTranslation(
            op.AIsPercent ? width * op.A / 100f : op.A,
            op.BIsPercent ? height * op.B / 100f : op.B),
        TransformOpType.TranslateX => SKMatrix.CreateTranslation(op.AIsPercent ? width * op.A / 100f : op.A, 0),
        TransformOpType.TranslateY => SKMatrix.CreateTranslation(0, op.AIsPercent ? height * op.A / 100f : op.A),
        TransformOpType.Scale => SKMatrix.CreateScale(op.A, op.B),
        TransformOpType.ScaleX => SKMatrix.CreateScale(op.A, 1),
        TransformOpType.ScaleY => SKMatrix.CreateScale(1, op.A),
        TransformOpType.Rotate => SKMatrix.CreateRotationDegrees(op.A),
        TransformOpType.Skew => SkewMatrix(op.A, op.B),
        TransformOpType.SkewX => SkewMatrix(op.A, 0),
        TransformOpType.SkewY => SkewMatrix(0, op.A),
        _ => SKMatrix.CreateIdentity()
    };

    private static SKMatrix SkewMatrix(float xDegrees, float yDegrees) => new()
    {
        ScaleX = 1, SkewY = MathF.Tan(DegToRad(yDegrees)), SkewX = MathF.Tan(DegToRad(xDegrees)), ScaleY = 1,
        TransX = 0, TransY = 0, Persp0 = 0, Persp1 = 0, Persp2 = 1
    };

    private static bool TryParseFunction(string function, out TransformOp? op)
    {
        op = null;
        var open = function.IndexOf('(');
        if (open <= 0 || !function.EndsWith(')')) return false;
        var name = function[..open].Trim().ToLowerInvariant();
        var args = function[(open + 1)..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (name)
        {
            case "matrix" when TryFloats(args, 6, out var m):
                op = new TransformOp(TransformOpType.Matrix, m[0], m[1], m[2], m[3], m[4], m[5]);
                return true;
            case "translate" when TryTranslateArgs(args, 2, out var tx, out var ty):
                op = new TransformOp(TransformOpType.Translate, tx.Value, ty.Value, AIsPercent: tx.IsPercent, BIsPercent: ty.IsPercent);
                return true;
            case "translatex" when TryTranslateArgs(args, 1, out var tx2, out _):
                op = new TransformOp(TransformOpType.TranslateX, tx2.Value, AIsPercent: tx2.IsPercent);
                return true;
            case "translatey" when TryTranslateArgs(args, 1, out var ty2, out _):
                op = new TransformOp(TransformOpType.TranslateY, ty2.Value, AIsPercent: ty2.IsPercent);
                return true;
            case "scale" when TryFloats(args, 2, out var s):
                op = new TransformOp(TransformOpType.Scale, s[0], s.Length > 1 ? s[1] : s[0]);
                return true;
            case "scalex" when TryFloats(args, 1, out var sx):
                op = new TransformOp(TransformOpType.ScaleX, sx[0]);
                return true;
            case "scaley" when TryFloats(args, 1, out var sy):
                op = new TransformOp(TransformOpType.ScaleY, sy[0]);
                return true;
            case "rotate" when args.Length == 1 && TryParseAngleDegrees(args[0], out var deg):
                op = new TransformOp(TransformOpType.Rotate, deg);
                return true;
            case "rotatez" when args.Length == 1 && TryParseAngleDegrees(args[0], out var degZ):
                op = new TransformOp(TransformOpType.Rotate, degZ);
                return true;
            case "skew" when TryParseAngleArgs(args, 2, out var k):
                op = new TransformOp(TransformOpType.Skew, k[0], k.Length > 1 ? k[1] : 0);
                return true;
            case "skewx" when args.Length == 1 && TryParseAngleDegrees(args[0], out var kx):
                op = new TransformOp(TransformOpType.SkewX, kx);
                return true;
            case "skewy" when args.Length == 1 && TryParseAngleDegrees(args[0], out var ky):
                op = new TransformOp(TransformOpType.SkewY, ky);
                return true;
            // Accepted 3D functions with no 2D effect.
            case "perspective" or "matrix3d" or "rotatex" or "rotatey" or "translatez" or "scalez" when args.Length > 0:
                return true;
            default:
                return false;
        }
    }

    private static bool TryTranslateArgs(string[] args, int max, out (float Value, bool IsPercent) x, out (float Value, bool IsPercent) y)
    {
        x = default;
        y = default;
        if (args.Length is < 1 or > 2) return false;
        if (!TryParseTranslateLength(args[0], out x)) return false;
        if (args.Length == 2 && !TryParseTranslateLength(args[1], out y)) return false;
        if (args.Length == 1) y = (0, false);
        return true;
    }

    private static bool TryParseTranslateLength(string value, out (float Value, bool IsPercent) result)
    {
        result = default;
        var trimmed = value.Trim();
        if (trimmed.EndsWith('%'))
        {
            if (!float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) return false;
            result = (percent, true);
            return true;
        }
        if (!CssValueParsers.TryParseTransformLength(trimmed, out var length)) return false;
        result = (length, false);
        return true;
    }

    /// <summary>Parses an angle in deg/rad/turn/grad or unitless (degrees).</summary>
    private static bool TryParseAngleDegrees(string value, out float degrees)
    {
        degrees = 0;
        var trimmed = value.Trim();
        if (trimmed.EndsWith("deg", StringComparison.OrdinalIgnoreCase))
            return float.TryParse(trimmed[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out degrees);
        // grad must be checked before rad: "100grad" ends with "rad".
        if (trimmed.EndsWith("grad", StringComparison.OrdinalIgnoreCase))
            return float.TryParse(trimmed[..^4], NumberStyles.Float, CultureInfo.InvariantCulture, out var grad) &&
                   (degrees = grad * 0.9f, true).Item2;
        if (trimmed.EndsWith("rad", StringComparison.OrdinalIgnoreCase))
            return float.TryParse(trimmed[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var rad) &&
                   (degrees = RadToDeg(rad), true).Item2;
        if (trimmed.EndsWith("turn", StringComparison.OrdinalIgnoreCase))
            return float.TryParse(trimmed[..^4], NumberStyles.Float, CultureInfo.InvariantCulture, out var turns) &&
                   (degrees = turns * 360, true).Item2;
        return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out degrees);
    }

    private static bool TryFloats(string[] args, int max, out float[] values)
    {
        values = [];
        if (args.Length is < 1 || args.Length > max) return false;
        values = new float[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            if (!float.TryParse(args[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return false;
        }
        return true;
    }

    private static bool TryParseAngleArgs(string[] args, int max, out float[] degrees)
    {
        degrees = [];
        if (args.Length is < 1 || args.Length > max) return false;
        degrees = new float[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            if (!TryParseAngleDegrees(args[i], out degrees[i])) return false;
        }
        return true;
    }

    private static bool TryLerpOp(TransformOp start, TransformOp end, float t, out TransformOp result)
    {
        result = default;
        if (start.Type != end.Type) return false;
        var translateLike = start.Type is TransformOpType.Translate or TransformOpType.TranslateX or TransformOpType.TranslateY;
        if (translateLike && (start.AIsPercent != end.AIsPercent || start.BIsPercent != end.BIsPercent)) return false;
        result = start with
        {
            A = start.A + (end.A - start.A) * t,
            B = start.B + (end.B - start.B) * t,
            C = start.C + (end.C - start.C) * t,
            D = start.D + (end.D - start.D) * t,
            E = start.E + (end.E - start.E) * t,
            F = start.F + (end.F - start.F) * t
        };
        return true;
    }

    /// <summary>Matrix-path interpolation: decompose → lerp components → recompose (CSS 2D matrix interpolation).</summary>
    private static TransformList LerpMatrices(TransformList from, TransformList to, float t)
    {
        var (fromTx, fromTy, fromRot, fromSx, fromSy, fromSkew) = Decompose(from);
        var (toTx, toTy, toRot, toSx, toSy, toSkew) = Decompose(to);
        return Recompose(
            fromTx + (toTx - fromTx) * t,
            fromTy + (toTy - fromTy) * t,
            fromRot + (toRot - fromRot) * t,
            fromSx + (toSx - fromSx) * t,
            fromSy + (toSy - fromSy) * t,
            fromSkew + (toSkew - fromSkew) * t);
    }

    /// <summary>
    /// Decomposes the 2D matrix into translate, rotate (radians), scale and skew
    /// (radians), following the CSS Transforms spec's decomposition.
    /// </summary>
    private static (float Tx, float Ty, float Rot, float Sx, float Sy, float Skew) Decompose(TransformList list)
    {
        var (a, b, c, d, e, f) = Accumulate(list);
        var row0x = a;
        var row0y = b;
        var row1x = c;
        var row1y = d;

        var scaleX = MathF.Sqrt(row0x * row0x + row0y * row0y);
        if (scaleX != 0)
        {
            row0x /= scaleX;
            row0y /= scaleX;
        }
        var shear = row0x * row1x + row0y * row1y;
        row1x -= row0x * shear;
        row1y -= row0y * shear;
        var scaleY = MathF.Sqrt(row1x * row1x + row1y * row1y);
        if (scaleY != 0)
        {
            row1x /= scaleY;
            row1y /= scaleY;
        }
        if (scaleY != 0) shear /= scaleY;
        var rot = MathF.Atan2(row0y, row0x);
        var skew = MathF.Atan(shear);
        return (e, f, rot, scaleX, scaleY, skew);
    }

    /// <summary>Recomposes decomposed components as <c>translate · rotate · skewX · scale</c> (CSS order).</summary>
    private static TransformList Recompose(float tx, float ty, float rot, float sx, float sy, float skew) => new(
    [
        new TransformOp(TransformOpType.Translate, tx, ty),
        new TransformOp(TransformOpType.Rotate, RadToDeg(rot)),
        new TransformOp(TransformOpType.SkewX, RadToDeg(skew)),
        new TransformOp(TransformOpType.Scale, sx, sy)
    ]);

    /// <summary>Accumulates the ops into a 2x3 matrix (a, b, c, d, e, f), left-to-right.</summary>
    private static (float A, float B, float C, float D, float E, float F) Accumulate(TransformList list)
    {
        float a = 1, b = 0, c = 0, d = 1, e = 0, f = 0;
        foreach (var op in list.Ops)
        {
            // Percentages cannot be resolved without the element size; the
            // numeric value is used (matched lists avoid this path entirely).
            var m = OpMatrix(op, 1, 1);
            (a, b, c, d, e, f) = Multiply(a, b, c, d, e, f, m.ScaleX, m.SkewY, m.SkewX, m.ScaleY, m.TransX, m.TransY);
        }
        return (a, b, c, d, e, f);
    }

    private static (float A, float B, float C, float D, float E, float F) Multiply(
        float a1, float b1, float c1, float d1, float e1, float f1,
        float a2, float b2, float c2, float d2, float e2, float f2) => (
        a1 * a2 + c1 * b2,
        b1 * a2 + d1 * b2,
        a1 * c2 + c1 * d2,
        b1 * c2 + d1 * d2,
        a1 * e2 + c1 * f2 + e1,
        b1 * e2 + d1 * f2 + f1);

    private static float DegToRad(float degrees) => degrees * MathF.PI / 180f;
    private static float RadToDeg(float radians) => radians * 180f / MathF.PI;
}

/// <summary>
/// The CSS <c>transform-origin</c> pivot: a length and/or percentage along each
/// axis, resolved against the element's box at render time. The default is the
/// box center (<c>50% 50%</c>).
/// </summary>
public readonly record struct TransformOrigin(float LengthX, float PercentX, float LengthY, float PercentY)
{
    public static TransformOrigin Center => new(0, 50, 0, 50);

    public float ResolveX(float width) => LengthX + width * PercentX / 100f;
    public float ResolveY(float height) => LengthY + height * PercentY / 100f;

    /// <summary>Parses <c>transform-origin</c>: one or two of px/percent or left/center/right/top/bottom.</summary>
    public static bool TryParse(string value, out TransformOrigin origin)
    {
        origin = Center;
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length is < 1 or > 2) return false;
        if (!TryParseComponent(tokens[0], out var x)) return false;
        (float Length, float Percent) y = (0, 50);
        if (tokens.Length == 2 && !TryParseComponent(tokens[1], out y)) return false;
        origin = new TransformOrigin(x.Length, x.Percent, y.Length, y.Percent);
        return true;
    }

    public static TransformOrigin Lerp(TransformOrigin from, TransformOrigin to, float t) => new(
        from.LengthX + (to.LengthX - from.LengthX) * t,
        from.PercentX + (to.PercentX - from.PercentX) * t,
        from.LengthY + (to.LengthY - from.LengthY) * t,
        from.PercentY + (to.PercentY - from.PercentY) * t);

    private static bool TryParseComponent(string token, out (float Length, float Percent) result)
    {
        result = default;
        var lower = token.ToLowerInvariant();
        if (lower is "left" or "top")
        {
            result = (0, 0);
            return true;
        }
        if (lower == "center")
        {
            result = (0, 50);
            return true;
        }
        if (lower is "right" or "bottom")
        {
            result = (0, 100);
            return true;
        }
        if (lower.EndsWith('%'))
        {
            if (!float.TryParse(lower[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) return false;
            result = (0, percent);
            return true;
        }
        if (!CssValueParsers.TryParseTransformLength(token, out var length)) return false;
        result = (length, 0);
        return true;
    }
}
