using System.Collections.Concurrent;
using System.Text;
using SixLabors.Fonts;

namespace Crowbar.UI;

/// <summary>
/// Shared text measurement and wrapping used by the layout engine so the
/// layout box always matches the glyphs the GPU renderer draws. Both measure
/// through SixLabors.Fonts with the same options (Dpi 72, standard kerning,
/// tracking from letter-spacing). Honors the typography computed properties:
/// font-family/weight, letter-spacing (tracking), text-transform, white-space
/// wrap modes and text-overflow ellipsis.
/// </summary>
internal static class TextLayout
{
    private static readonly string[] FallbackFamilies =
        ["Segoe UI", "Arial", "DejaVu Sans", "Liberation Sans", "Helvetica", "Roboto"];

    // FontFamilies are immutable managed values and safe to share, so they are
    // cached the same way the GPU renderer's font manager resolves
    // (family, weight) pairs. This keeps layout measurement and glyph
    // rasterization in agreement. The cache is concurrent because the parallel
    // test runner (and any off-thread layout) resolves fonts concurrently.
    private static readonly ConcurrentDictionary<(string Family, int Weight), FontFamily> FamilyCache = new();

    /// <summary>Creates the font for a computed style (size, family and weight).</summary>
    public static Font CreateFont(ComputedStyle style) =>
        ResolveFamily(style.FontFamily, style.FontWeight).CreateFont(
            style.FontSize, style.FontWeight >= 600 ? FontStyle.Bold : FontStyle.Regular);

    private static FontFamily ResolveFamily(string family, int weight)
    {
        var key = (family, weight);
        if (FamilyCache.TryGetValue(key, out var cached)) return cached;

        FontFamily? resolved = null;
        if (!string.IsNullOrWhiteSpace(family) && SystemFonts.TryGet(family, out var requested))
        {
            resolved = requested;
        }
        else
        {
            foreach (var name in FallbackFamilies)
            {
                if (SystemFonts.TryGet(name, out var fallback))
                {
                    resolved = fallback;
                    break;
                }
            }
            if (resolved is null)
            {
                foreach (var available in SystemFonts.Families)
                {
                    resolved = available;
                    break;
                }
            }
        }

        if (resolved is not { } result)
            throw new InvalidOperationException("No system fonts are available for text measurement.");

        FamilyCache[key] = result;
        return result;
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
    /// Measures a single line with the same options the GPU renderer uses, so
    /// layout and painted glyphs agree. Letter-spacing becomes SixLabors
    /// tracking (an em multiplier).
    /// </summary>
    public static float Measure(Font font, string text, float letterSpacing)
    {
        var options = new TextOptions(font)
        {
            Dpi = 72,
            KerningMode = KerningMode.Standard,
            ColorFontSupport = ColorFontSupport.None
        };
        if (letterSpacing != 0f)
            options.Tracking = letterSpacing / font.Size;
        return TextMeasurer.MeasureAdvance(text, options).Width;
    }

    /// <summary>
    /// Splits text into display lines following <c>white-space</c>: normal /
    /// pre-line wrap on word boundaries, pre-wrap on character boundaries, pre
    /// and nowrap only on explicit newlines. When <c>text-overflow</c> is
    /// <c>ellipsis</c>, overflowing lines are truncated with a trailing ellipsis.
    /// </summary>
    public static List<string> Wrap(string text, Font font, float width, string whiteSpace, float letterSpacing,
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
