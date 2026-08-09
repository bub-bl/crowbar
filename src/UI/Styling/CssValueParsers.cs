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

    /// <summary>
    /// Parses a duration, expressed in seconds (<c>200ms</c> or <c>0.2s</c>).
    /// Durations are clamped to zero unless <paramref name="allowNegative"/> is
    /// set (negative delays are valid CSS).
    /// </summary>
    public static bool TryParseTime(string value, out float result, bool allowNegative = false)
    {
        result = 0;
        value = value.Trim().ToLowerInvariant();
        var multiplier = value.EndsWith("ms", StringComparison.Ordinal) ? 0.001f : 1f;
        value = value.TrimEnd('m', 's');
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return false;
        result = seconds * multiplier;
        if (!allowNegative) result = Math.Max(0, result);
        return true;
    }

    /// <summary>
    /// Parses a transform length: a px length or unitless number that may be
    /// negative (unlike layout lengths, transform offsets are not clamped).
    /// </summary>
    public static bool TryParseTransformLength(string value, out float result)
    {
        result = 0;
        var trimmed = value.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^2];
        return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>Parses an angle in degrees (<c>45deg</c> or a unitless number).</summary>
    public static bool TryParseAngle(string value, out float result)
    {
        result = 0;
        var trimmed = value.Trim();
        if (trimmed.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^3];
        else if (trimmed.EndsWith("turn", StringComparison.OrdinalIgnoreCase))
        {
            if (!float.TryParse(trimmed[..^4], NumberStyles.Float, CultureInfo.InvariantCulture, out var turns)) return false;
            result = turns * 360;
            return true;
        }
        return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Parses a <c>box-shadow</c> list (<c>none</c> or comma-separated shadows).
    /// Each shadow is <c>inset? &lt;x&gt; &lt;y&gt; &lt;blur&gt;? &lt;spread&gt;? &lt;color&gt;?</c>
    /// with the components in any order; offsets and spread may be negative.
    /// </summary>
    public static bool TryParseBoxShadows(string value, out BoxShadow[] shadows)
    {
        shadows = [];
        var trimmed = value.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = SplitShadowList(trimmed);
        if (parts.Length == 0) return false;
        var list = new List<BoxShadow>(parts.Length);
        foreach (var part in parts)
        {
            if (!TryParseBoxShadow(part, out var shadow)) return false;
            list.Add(shadow);
        }
        shadows = list.ToArray();
        return true;
    }

    /// <summary>
    /// Parses a <c>text-shadow</c> list (<c>none</c> or comma-separated shadows).
    /// Each shadow is <c>&lt;color&gt;? &lt;x&gt; &lt;y&gt; &lt;blur&gt;?</c> with the
    /// components in any order; offsets may be negative.
    /// </summary>
    public static bool TryParseTextShadows(string value, out TextShadow[] shadows)
    {
        shadows = [];
        var trimmed = value.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = SplitShadowList(trimmed);
        if (parts.Length == 0) return false;
        var list = new List<TextShadow>(parts.Length);
        foreach (var part in parts)
        {
            if (!TryParseTextShadow(part, out var shadow)) return false;
            list.Add(shadow);
        }
        shadows = list.ToArray();
        return true;
    }

    private static bool TryParseBoxShadow(string value, out BoxShadow shadow)
    {
        shadow = default;
        var inset = false;
        var lengths = new List<float>(4);
        var color = UiColor.Black;
        foreach (var token in TokenizeShadow(value))
        {
            if (token.Equals("inset", StringComparison.OrdinalIgnoreCase))
            {
                inset = true;
            }
            else if (UiColor.TryParse(token, out var parsedColor))
            {
                color = parsedColor;
            }
            else if (TryParseOffsetLength(token, out var length))
            {
                lengths.Add(length);
            }
            else return false;
        }
        if (lengths.Count is < 2 or > 4) return false;
        shadow = new BoxShadow(
            lengths[0], lengths[1],
            lengths.Count > 2 ? Math.Max(0, lengths[2]) : 0,
            lengths.Count > 3 ? lengths[3] : 0,
            color, inset);
        return true;
    }

    private static bool TryParseTextShadow(string value, out TextShadow shadow)
    {
        shadow = default;
        var lengths = new List<float>(3);
        var color = UiColor.Black;
        foreach (var token in TokenizeShadow(value))
        {
            if (UiColor.TryParse(token, out var parsedColor))
            {
                color = parsedColor;
            }
            else if (TryParseOffsetLength(token, out var length))
            {
                lengths.Add(length);
            }
            else return false;
        }
        if (lengths.Count is < 2 or > 3) return false;
        shadow = new TextShadow(
            lengths[0], lengths[1],
            lengths.Count > 2 ? Math.Max(0, lengths[2]) : 0,
            color);
        return true;
    }

    /// <summary>Splits a shadow list on top-level commas (ignores commas inside rgb()/hsl()/...).</summary>
    private static string[] SplitShadowList(string value)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(value[start..i]);
                start = i + 1;
            }
        }
        parts.Add(value[start..]);
        return parts.Where(static part => !string.IsNullOrWhiteSpace(part)).ToArray();
    }

    /// <summary>
    /// Splits a single shadow into whitespace-separated tokens, keeping
    /// parenthesized groups (<c>rgba(0, 0, 0, 0.5)</c>) together so function
    /// colors survive the tokenization.
    /// </summary>
    private static string[] TokenizeShadow(string value)
    {
        var tokens = new List<string>();
        var depth = 0;
        var start = -1;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (start >= 0)
                {
                    tokens.Add(value[start..i]);
                    start = -1;
                }
            }
            else if (start < 0) start = i;
        }
        if (start >= 0) tokens.Add(value[start..]);
        return tokens.ToArray();
    }

    /// <summary>
    /// Splits a value on whitespace, keeping parenthesized groups together so
    /// function values such as <c>steps(4, end)</c> or
    /// <c>cubic-bezier(0.1, 0.2, 0.3, 0.4)</c> survive the tokenization.
    /// </summary>
    public static string[] SplitWhitespaceTokens(string value)
    {
        var tokens = new List<string>();
        var depth = 0;
        var start = -1;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (start >= 0)
                {
                    tokens.Add(value[start..i]);
                    start = -1;
                }
            }
            else if (start < 0) start = i;
        }
        if (start >= 0) tokens.Add(value[start..]);
        return tokens.ToArray();
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
