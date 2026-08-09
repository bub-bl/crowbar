using System.Globalization;

namespace Crowbar.UI;

/// <summary>
/// CSS timing functions (easing curves) shared by transitions and keyframe
/// animations. Every named keyword plus <c>cubic-bezier(...)</c> and
/// <c>steps(...)</c> are supported; unknown values fall back to linear,
/// mirroring CSS invalid-value handling.
/// </summary>
public static class TimingFunctions
{
    /// <summary>
    /// Evaluates a timing function for a linear progress <paramref name="t"/>
    /// in [0, 1]. The result is clamped to [0, 1] (cubic-bezier curves may
    /// overshoot when their y control points leave the unit square).
    /// </summary>
    public static float Evaluate(string timingFunction, float t)
    {
        var name = timingFunction.Trim().ToLowerInvariant();
        if (name.StartsWith("steps(", StringComparison.Ordinal) && name.EndsWith(')'))
        {
            var args = name["steps(".Length..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (args.Length is 1 or 2 &&
                int.TryParse(args[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) &&
                count > 0)
            {
                var jumpStart = args.Length == 2 && IsStartKeyword(args[1]);
                return Steps(count, jumpStart, t);
            }
        }
        // Step keywords must be evaluated before the continuous clamp: their
        // boundary values (1 at t = 0 for step-start, 0 until t = 1 for
        // step-end) differ from the curves.
        if (name == "step-start") return 1;
        if (name == "step-end") return t >= 1 ? 1 : 0;
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        if (name.StartsWith("cubic-bezier(", StringComparison.Ordinal) && name.EndsWith(')'))
        {
            var args = name["cubic-bezier(".Length..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (args.Length == 4 && TryParseFloats(args, out var values))
                return Math.Clamp(CubicBezier(values[0], values[1], values[2], values[3], t), 0f, 1f);
        }
        return name switch
        {
            "linear" => t,
            "ease" => CubicBezier(0.25f, 0.1f, 0.25f, 1f, t),
            "ease-in" => CubicBezier(0.42f, 0f, 1f, 1f, t),
            "ease-out" => CubicBezier(0f, 0f, 0.58f, 1f, t),
            "ease-in-out" => CubicBezier(0.42f, 0f, 0.58f, 1f, t),
            _ => t
        };
    }

    /// <summary>True when the value names a supported timing function keyword.</summary>
    public static bool IsKeyword(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        return lower is "linear" or "ease" or "ease-in" or "ease-out" or "ease-in-out" or "step-start" or "step-end" ||
               lower.StartsWith("cubic-bezier(", StringComparison.Ordinal) ||
               lower.StartsWith("steps(", StringComparison.Ordinal);
    }

    /// <summary>
    /// Evaluates a cubic Bézier easing curve with control points
    /// (x1, y1, x2, y2) at linear progress <paramref name="t"/>. The curve's x
    /// coordinate is inverted (Newton–Raphson with a bisection fallback) so the
    /// output is y at the curve parameter where x equals t.
    /// </summary>
    public static float CubicBezier(float x1, float y1, float x2, float y2, float t)
    {
        var curve = t;
        for (var i = 0; i < 8; i++)
        {
            var err = BezierX(x1, x2, curve) - t;
            if (Math.Abs(err) < 1e-6f) break;
            var slope = BezierDx(x1, x2, curve);
            if (Math.Abs(slope) < 1e-9f) break;
            curve = Math.Clamp(curve - err / slope, 0f, 1f);
        }
        if (Math.Abs(BezierX(x1, x2, curve) - t) > 1e-3f)
        {
            // Bisection fallback when Newton leaves the valid range.
            var lo = 0f;
            var hi = 1f;
            for (var i = 0; i < 16; i++)
            {
                var mid = (lo + hi) / 2f;
                if (BezierX(x1, x2, mid) < t) lo = mid;
                else hi = mid;
            }
            curve = (lo + hi) / 2f;
        }
        return BezierY(y1, y2, curve);
    }

    /// <summary>
    /// Evaluates a <c>steps()</c> timing function: <paramref name="count"/>
    /// discrete jumps of equal size. With <paramref name="jumpStart"/> (the
    /// <c>start</c>/<c>jump-start</c> keyword) the first jump happens at
    /// t = 0; otherwise (the default <c>end</c>/<c>jump-end</c>) at t = 1/n.
    /// </summary>
    public static float Steps(int count, bool jumpStart, float t)
    {
        if (count <= 1) return jumpStart ? (t < 1 ? 0 : 1) : (t > 0 ? 1 : 0);
        var step = MathF.Floor(t * count);
        var value = step / count;
        return jumpStart ? Math.Min(1f, value + 1f / count) : value;
    }

    private static float BezierX(float x1, float x2, float t) =>
        (1 - t) * (1 - t) * (1 - t) * 0f + 3 * (1 - t) * (1 - t) * t * x1 + 3 * (1 - t) * t * t * x2 + t * t * t;

    private static float BezierDx(float x1, float x2, float t) =>
        3 * (1 - t) * (1 - t) * x1 + 6 * (1 - t) * t * (x2 - x1) + 3 * t * t * (1 - x2);

    private static float BezierY(float y1, float y2, float t) =>
        3 * (1 - t) * (1 - t) * t * y1 + 3 * (1 - t) * t * t * y2 + t * t * t;

    private static bool IsStartKeyword(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        return lower is "start" or "jump-start";
    }

    private static bool TryParseFloats(string[] values, out float[] result)
    {
        result = new float[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (!float.TryParse(values[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                return false;
        }
        return true;
    }
}
