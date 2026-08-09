using System.Globalization;

namespace Crowbar.UI;

/// <summary>
/// Small, reusable parsers for the value types the styling engine accepts.
/// They double as the default parsers of the built-in <see cref="CssProperty"/>
/// registrations and are exposed for custom property implementations.
/// </summary>
public static class CssValueParsers
{
    /// <summary>Parses a plain number (flex-grow, opacity, ...).</summary>
    public static bool TryParseNumber(string value, out float result)
    {
        result = 0;
        return float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>Parses a CSS length, clamped to zero (unitless values are treated as pixels).</summary>
    public static bool TryParseLength(string value, out float result)
    {
        result = 0;
        if (!TryParseDimension(value, out var length) || length is null) return false;
        result = length.Value;
        return true;
    }

    /// <summary>Parses a nullable CSS length (null means "unspecified"), clamped to zero.</summary>
    public static bool TryParseDimension(string value, out float? result)
    {
        result = null;
        var trimmed = value.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^2];
        if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return false;
        result = Math.Max(0, n);
        return true;
    }

    /// <summary>
    /// Parses a CSS length with its unit: pixels/unitless numbers, percentages,
    /// <c>auto</c> and the content-based keywords (<c>max-content</c>,
    /// <c>fit-content</c>). The <paramref name="allowAuto"/> and
    /// <paramref name="allowContent"/> flags restrict which keywords the
    /// property accepts (e.g. padding cannot be <c>auto</c>).
    /// </summary>
    public static bool TryParseCssLength(string value, out CssLength result, bool allowAuto = true, bool allowContent = true)
    {
        result = CssLength.Undefined;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return false;
        if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowAuto) return false;
            result = CssLength.Auto;
            return true;
        }
        if (trimmed.Equals("max-content", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowContent) return false;
            result = CssLength.MaxContent;
            return true;
        }
        if (trimmed.Equals("fit-content", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowContent) return false;
            result = CssLength.FitContent;
            return true;
        }
        if (trimmed.EndsWith('%'))
        {
            if (!float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) return false;
            result = CssLength.Percent(Math.Max(0, percent));
            return true;
        }
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^2];
        if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var points)) return false;
        result = CssLength.Points(Math.Max(0, points));
        return true;
    }

    /// <summary>
    /// Parses <c>scrollbar-width</c>: <c>auto</c> (0, engine default),
    /// <c>thin</c> (6px) or an explicit length in px.
    /// </summary>
    public static bool TryParseScrollbarWidth(string value, out float result)
    {
        result = 0;
        var trimmed = value.Trim();
        if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("thin", StringComparison.OrdinalIgnoreCase))
        {
            result = 6;
            return true;
        }
        return TryParseLength(trimmed, out result);
    }

    /// <summary>
    /// Parses <c>outline-width</c>: the CSS keywords <c>thin</c> (1px),
    /// <c>medium</c> (3px), <c>thick</c> (5px) or an explicit length in px.
    /// </summary>
    public static bool TryParseOutlineWidth(string value, out float result)
    {
        result = 0;
        var trimmed = value.Trim().ToLowerInvariant();
        if (trimmed == "thin")
        {
            result = 1;
            return true;
        }
        if (trimmed == "medium")
        {
            result = 3;
            return true;
        }
        if (trimmed == "thick")
        {
            result = 5;
            return true;
        }
        return TryParseLength(trimmed, out result);
    }

    /// <summary>
    /// Parses <c>outline-offset</c>: a px length that may be negative (the
    /// outline can be drawn inside the border box).
    /// </summary>
    public static bool TryParseOffsetLength(string value, out float result)
    {
        result = 0;
        var trimmed = value.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^2];
        return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>Parses a duration, expressed in seconds (<c>200ms</c> or <c>0.2s</c>).</summary>
    public static bool TryParseTime(string value, out float result)
    {
        result = 0;
        value = value.Trim().ToLowerInvariant();
        var multiplier = value.EndsWith("ms", StringComparison.Ordinal) ? 0.001f : 1f;
        value = value.TrimEnd('m', 's');
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return false;
        result = Math.Max(0, seconds * multiplier);
        return true;
    }

    /// <summary>Parses the 1 to 4 value box shorthand (margin/padding), lengths may be <c>auto</c>.</summary>
    public static bool TryParseLengthBox(string value, out BoxValues<CssLength> box, bool allowAuto = false)
    {
        box = default;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4) return false;
        var parsed = new CssLength[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!TryParseCssLength(parts[i], out parsed[i], allowAuto, allowContent: false)) return false;

        var top = parsed[0];
        var right = parts.Length > 1 ? parsed[1] : top;
        var bottom = parts.Length > 2 ? parsed[2] : top;
        var left = parts.Length > 3 ? parsed[3] : right;
        box = new BoxValues<CssLength>(top, right, bottom, left);
        return true;
    }
}

/// <summary>Resolved box-shorthand values (top, right, bottom, left).</summary>
public readonly record struct BoxValues<T>(T Top, T Right, T Bottom, T Left);
