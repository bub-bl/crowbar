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
    /// Splits a value on a top-level separator (e.g. the commas of
    /// <c>animation: fade 1s, pulse 2s</c>), ignoring separators inside
    /// parentheses so function values such as <c>steps(4, end)</c> survive.
    /// </summary>
    public static string[] SplitTopLevel(string value, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == separator && depth == 0)
            {
                parts.Add(value[start..i]);
                start = i + 1;
            }
        }
        parts.Add(value[start..]);
        return parts.Where(static part => !string.IsNullOrWhiteSpace(part))
            .Select(static part => part.Trim()).ToArray();
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

    /// <summary>
    /// Parses an image reference: <c>none</c> (null) or <c>url(...)</c> with an
    /// optional quoted or unquoted URL. Used by <c>background-image</c>.
    /// </summary>
    public static bool TryParseUrl(string value, out string? url)
    {
        url = null;
        var trimmed = value.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Length < 6 ||
            !trimmed.StartsWith("url(", StringComparison.OrdinalIgnoreCase) ||
            !trimmed.EndsWith(')')) return false;
        var inner = trimmed[4..^1].Trim();
        if (inner.Length >= 2 && inner[0] is '"' or '\'' && inner[^1] == inner[0]) inner = inner[1..^1];
        if (string.IsNullOrEmpty(inner)) return false;
        url = inner;
        return true;
    }

    /// <summary>
    /// Parses a CSS position (<c>background-position</c>, <c>object-position</c>):
    /// one or two components, each a keyword (<c>left</c>/<c>center</c>/<c>right</c>/
    /// <c>top</c>/<c>bottom</c>), percentage or length (negative lengths allowed).
    /// Keywords become percentages; a single value centers the missing axis.
    /// </summary>
    public static bool TryParseCssPosition(string value, out CssPosition position)
    {
        position = CssPosition.Center;
        var parts = SplitWhitespaceTokens(value);
        if (parts.Length is < 1 or > 2) return false;

        // Resolves one component: its horizontal value (or undefined), its
        // vertical value (or undefined), whether it is a top/bottom keyword
        // (which pins the vertical axis) and whether it is `center` (which
        // occupies both axes).
        static bool ParseComponent(string token, out CssLength x, out CssLength y,
            out bool verticalKeyword, out bool center)
        {
            x = CssLength.Undefined;
            y = CssLength.Undefined;
            verticalKeyword = false;
            center = false;
            switch (token.ToLowerInvariant())
            {
                case "left": x = CssLength.Percent(0); return true;
                case "right": x = CssLength.Percent(100); return true;
                case "top": y = CssLength.Percent(0); verticalKeyword = true; return true;
                case "bottom": y = CssLength.Percent(100); verticalKeyword = true; return true;
                case "center": x = CssLength.Percent(50); y = CssLength.Percent(50); center = true; return true;
                default: return TryParsePositionComponent(token, out x);
            }
        }

        if (parts.Length == 1)
        {
            if (!ParseComponent(parts[0], out var x, out var y, out var vertical, out _)) return false;
            position = vertical ? new CssPosition(CssLength.Percent(50), y) : new CssPosition(x, CssLength.Percent(50));
            return true;
        }

        if (!ParseComponent(parts[0], out var x1, out var y1, out var vk1, out var c1) ||
            !ParseComponent(parts[1], out var x2, out var y2, out var vk2, out var c2)) return false;

        // A top/bottom keyword pins the vertical axis; the other value is
        // horizontal (center supplies 50%).
        if (vk1) { if (vk2) return false; position = new CssPosition(x2.IsDefined ? x2 : CssLength.Percent(50), y1); return true; }
        if (vk2) { if (vk1) return false; position = new CssPosition(x1.IsDefined ? x1 : CssLength.Percent(50), y2); return true; }

        // Otherwise the first value is horizontal and the second vertical.
        // `center` may occupy either axis: `center left` reads (left, center)
        // and `left center` reads (left, center) too, while a length or
        // percentage after `center` is the vertical axis (`center 10px`).
        if (c1 && !c2)
        {
            if (parts[1].Equals("left", StringComparison.OrdinalIgnoreCase) ||
                parts[1].Equals("right", StringComparison.OrdinalIgnoreCase))
                position = new CssPosition(x2, CssLength.Percent(50));
            else position = new CssPosition(CssLength.Percent(50), x2);
            return true;
        }
        if (c2 && !c1)
        {
            position = new CssPosition(x1, CssLength.Percent(50));
            return true;
        }
        position = new CssPosition(x1, x2);
        return true;
    }

    /// <summary>
    /// Parses <c>background-size</c>: <c>cover</c>, <c>contain</c>, or one/two
    /// <c>auto</c> | length | percentage values (negative lengths are clamped).
    /// </summary>
    public static bool TryParseBackgroundSize(string value, out BackgroundSize size)
    {
        size = BackgroundSize.AutoAuto;
        var parts = SplitWhitespaceTokens(value);
        if (parts.Length is < 1 or > 2) return false;
        var first = parts[0].ToLowerInvariant();
        if (first is "cover" or "contain")
        {
            if (parts.Length != 1) return false;
            size = new BackgroundSize(
                first == "cover" ? BackgroundSizeType.Cover : BackgroundSizeType.Contain,
                CssLength.Undefined, CssLength.Undefined);
            return true;
        }
        if (!TryParseSizeComponent(parts[0], out var width)) return false;
        var height = CssLength.Auto;
        if (parts.Length == 2 && !TryParseSizeComponent(parts[1], out height)) return false;
        size = new BackgroundSize(BackgroundSizeType.Explicit, width, height);
        return true;
    }

    /// <summary>
    /// Parses <c>background-repeat</c>: one or two <c>repeat</c> / <c>no-repeat</c>
    /// / <c>space</c> / <c>round</c> values, or the single-axis keywords
    /// <c>repeat-x</c> and <c>repeat-y</c>.
    /// </summary>
    public static bool TryParseRepeat(string value, out CssRepeat repeat)
    {
        repeat = CssRepeat.Repeat;
        var parts = SplitWhitespaceTokens(value);
        if (parts.Length is < 1 or > 2) return false;
        var lower = parts[0].ToLowerInvariant();
        if (lower is "repeat-x" or "repeat-y")
        {
            if (parts.Length != 1) return false;
            repeat = lower == "repeat-x"
                ? new CssRepeat(RepeatMode.Repeat, RepeatMode.NoRepeat)
                : new CssRepeat(RepeatMode.NoRepeat, RepeatMode.Repeat);
            return true;
        }
        if (!TryParseRepeatMode(parts[0], out var x)) return false;
        var y = x;
        if (parts.Length == 2 && !TryParseRepeatMode(parts[1], out y)) return false;
        repeat = new CssRepeat(x, y);
        return true;
    }

    /// <summary>Parses one background-size component: <c>auto</c>, percentage or px length (clamped to zero).</summary>
    private static bool TryParseSizeComponent(string value, out CssLength result)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            result = CssLength.Auto;
            return true;
        }
        if (!TryParsePositionComponent(value, out result)) return false;
        result = result.Unit == CssLengthUnit.Percent
            ? CssLength.Percent(Math.Max(0, result.Value))
            : CssLength.Points(Math.Max(0, result.Value));
        return true;
    }

    /// <summary>Parses a raw position component: a percentage or px length that may be negative.</summary>
    private static bool TryParsePositionComponent(string value, out CssLength result)
    {
        result = CssLength.Undefined;
        var trimmed = value.Trim();
        if (trimmed.EndsWith('%'))
        {
            if (!float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) return false;
            result = CssLength.Percent(percent);
            return true;
        }
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^2];
        if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var points)) return false;
        result = CssLength.Points(points);
        return true;
    }

    private static bool TryParseRepeatMode(string token, out RepeatMode mode)
    {
        switch (token.ToLowerInvariant())
        {
            case "repeat": mode = RepeatMode.Repeat; return true;
            case "no-repeat": mode = RepeatMode.NoRepeat; return true;
            case "space": mode = RepeatMode.Space; return true;
            case "round": mode = RepeatMode.Round; return true;
            default: mode = RepeatMode.Repeat; return false;
        }
    }
}

/// <summary>Resolved box-shorthand values (top, right, bottom, left).</summary>
public readonly record struct BoxValues<T>(T Top, T Right, T Bottom, T Left);
