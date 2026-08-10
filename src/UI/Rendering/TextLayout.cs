using System.Text;
using SkiaSharp;

namespace Crowbar.UI;

/// <summary>
/// Shared text measurement and wrapping used by the layout engine and the
/// renderer so the layout box always matches the drawn glyphs. Honors the
/// typography computed properties: font-family/weight (typeface), letter-spacing
/// (tracking), text-transform, white-space wrap modes and text-overflow
/// ellipsis.
/// </summary>
internal static class TextLayout
{
    /// <summary>
    /// Creates the font for a computed style (size, family and weight). A fresh
    /// SKFont is created per call (SkiaSharp's native handles are not safe to
    /// share or reuse across contexts, so the previous per-thread cache caused
    /// access violations under the parallel test runner); only the resolved
    /// typeface is cached, which is immutable and safe to share.
    /// </summary>
    public static SKFont CreateFont(ComputedStyle style) =>
        new() { Size = style.FontSize, Typeface = CreateTypeface(style.FontFamily, style.FontWeight) };

    [ThreadStatic] private static Dictionary<(string Family, int Weight), SKTypeface>? TypefaceCache;

    /// <summary>Resolves the family/weight combination, falling back to the default typeface.</summary>
    public static SKTypeface CreateTypeface(string family, int weight)
    {
        var cache = TypefaceCache ??= [];
        if (cache.TryGetValue((family, weight), out var cached)) return cached;
        var typeface = SKTypeface.FromFamilyName(family,
            new SKFontStyle((SKFontStyleWeight)weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)) ?? SKTypeface.Default;
        cache[(family, weight)] = typeface;
        return typeface;
    }

    /// <summary>Applies <c>text-transform</c> (none, uppercase, lowercase, capitalize).</summary>
    public static string ApplyTransform(string text, string transform)
    {
        switch (transform.ToLowerInvariant())
        {
            case "uppercase": return text.ToUpperInvariant();
            case "lowercase": return text.ToLowerInvariant();
            case "capitalize":
            {
                var sb = new StringBuilder(text.Length);
                var capitalize = true;
                foreach (var c in text)
                {
                    sb.Append(capitalize ? char.ToUpperInvariant(c) : c);
                    capitalize = char.IsWhiteSpace(c);
                }

                return sb.ToString();
            }
            default: return text;
        }
    }

    /// <summary>
    /// Measures text honoring letter-spacing: tracking adds between glyphs
    /// (<c>MeasureText</c> already sums the per-glyph advances).
    /// </summary>
    public static float Measure(SKFont font, string text, float letterSpacing) =>
        letterSpacing == 0 || text.Length <= 1
            ? font.MeasureText(text)
            : font.MeasureText(text) + letterSpacing * (text.Length - 1);

    /// <summary>
    /// Draws text, advancing manually per glyph when tracking is active
    /// (Skia has no native letter-spacing support).
    /// </summary>
    public static void Draw(SKCanvas canvas, string text, float x, float baseline, SKFont font, SKPaint paint,
        float letterSpacing)
    {
        if (letterSpacing == 0 || text.Length <= 1)
        {
            canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
            return;
        }

        var cursor = x;
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i].ToString();
            canvas.DrawText(character, cursor, baseline, SKTextAlign.Left, font, paint);
            cursor += font.MeasureText(character) + letterSpacing;
        }
    }

    /// <summary>
    /// Splits text into display lines following <c>white-space</c>: normal /
    /// pre-line wrap on word boundaries, pre-wrap on character boundaries, pre
    /// and nowrap only on explicit newlines. When <c>text-overflow</c> is
    /// <c>ellipsis</c>, overflowing lines are truncated with a trailing ellipsis.
    /// </summary>
    public static List<string> Wrap(string text, SKFont font, float width, string whiteSpace, float letterSpacing,
        string textOverflow)
    {
        var lines = new List<string>();
        // Fast path: the common case (no explicit newline) avoids the Split
        // allocation entirely when the text fits the box.
        if (text.IndexOf('\n') < 0 && (width <= 0 || Measure(font, text, letterSpacing) <= width))
        {
            if (textOverflow.Equals("ellipsis", StringComparison.OrdinalIgnoreCase))
                lines.Add(text);
            else
                return [text];
        }
        if (whiteSpace.Equals("pre", StringComparison.OrdinalIgnoreCase) ||
            whiteSpace.Equals("nowrap", StringComparison.OrdinalIgnoreCase))
        {
            lines.AddRange(text.Split('\n'));
        }
        else if (whiteSpace.Equals("pre-wrap", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var raw in text.Split('\n'))
            {
                if (width <= 0 || Measure(font, raw, letterSpacing) <= width)
                {
                    lines.Add(raw);
                    continue;
                }

                var current = new StringBuilder();
                foreach (var c in raw)
                {
                    var candidate = current.Append(c).ToString();
                    if (Measure(font, candidate, letterSpacing) > width)
                    {
                        current.Length--;
                        lines.Add(current.ToString());
                        current.Clear().Append(c);
                    }
                }

                lines.Add(current.ToString());
            }
        }
        else
        {
            // normal / pre-line: wrap on word boundaries.
            foreach (var raw in text.Split('\n'))
            {
                if (width <= 0 || Measure(font, raw, letterSpacing) <= width)
                {
                    lines.Add(raw);
                    continue;
                }

                var current = string.Empty;
                foreach (var word in raw.Split(' '))
                {
                    var candidate = current.Length == 0 ? word : current + " " + word;
                    if (current.Length > 0 && Measure(font, candidate, letterSpacing) > width)
                    {
                        lines.Add(current);
                        current = word;
                    }
                    else current = candidate;
                }

                if (current.Length > 0) lines.Add(current);
            }
        }

        if (lines.Count == 0) lines.Add(string.Empty);
        if (textOverflow.Equals("ellipsis", StringComparison.OrdinalIgnoreCase))
        {
            const string dots = "\u2026";
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (width <= 0 || Measure(font, line, letterSpacing) <= width) continue;
                var truncated = line;
                while (truncated.Length > 0 && Measure(font, truncated + dots, letterSpacing) > width)
                    truncated = truncated[..^1];
                lines[i] = truncated + dots;
            }
        }

        return lines;
    }
}
