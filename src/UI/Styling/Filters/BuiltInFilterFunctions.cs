using System.Globalization;
using System.Text;

namespace Crowbar.UI;

/// <summary>
/// The standard CSS filter functions (<c>blur</c>, <c>brightness</c>,
/// <c>contrast</c>, <c>drop-shadow</c>, <c>grayscale</c>, <c>hue-rotate</c>,
/// <c>invert</c>, <c>opacity</c>, <c>saturate</c>, <c>sepia</c>) registered at
/// startup. They are ordinary <see cref="FilterFunctionDefinition"/>s that only
/// parse CSS syntax; the actual color/effect math lives in the GPU renderer's
/// filter pass. A custom filter can be added the same way by calling
/// <see cref="CssFilterFunctions.Register"/>.
/// </summary>
internal static class BuiltInFilterFunctions
{
    public static void RegisterAll()
    {
        Register(new BlurFilter());
        Register(new BrightnessFilter());
        Register(new ContrastFilter());
        Register(new DropShadowFilter());
        Register(new GrayscaleFilter());
        Register(new HueRotateFilter());
        Register(new InvertFilter());
        Register(new OpacityFilter());
        Register(new SaturateFilter());
        Register(new SepiaFilter());
    }

    private static void Register(FilterFunctionDefinition definition) => CssFilterFunctions.Register(definition);

    /// <summary>blur(radius): Gaussian blur whose standard deviation is the radius.</summary>
    private sealed class BlurFilter : FilterFunctionDefinition
    {
        public BlurFilter() : base("blur") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseLength(arguments, out var radius)) return false;
            function = new CssFilterFunction(Name, [radius]);
            return true;
        }
    }

    /// <summary>brightness(amount): scales the RGB channels (values over 100% allowed).</summary>
    private sealed class BrightnessFilter : FilterFunctionDefinition
    {
        public BrightnessFilter() : base("brightness") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [amount]);
            return true;
        }
    }

    /// <summary>contrast(amount): scales the deviation from mid-gray.</summary>
    private sealed class ContrastFilter : FilterFunctionDefinition
    {
        public ContrastFilter() : base("contrast") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [amount]);
            return true;
        }
    }

    /// <summary>drop-shadow(x y [blur] [color]): a blurred shadow of the element's alpha.</summary>
    private sealed class DropShadowFilter : FilterFunctionDefinition
    {
        public DropShadowFilter() : base("drop-shadow") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            var tokens = FilterArgumentParser.SplitTopLevel(arguments);
            if (tokens.Count is < 2 or > 4) return false;

            // Lengths and the color may appear in either order (CSS && grammar),
            // so collect them independently. Whitespace inside a color like
            // rgb(0 0 0 / 50%) is preserved because SplitTopLevel keeps it.
            // The two offsets may be negative; the blur radius cannot.
            var lengths = new List<float>();
            var colorTokens = new List<string>();
            foreach (var token in tokens)
            {
                if (lengths.Count < 2 && FilterArgumentParser.TryParseSignedLength(token, out var offset)) lengths.Add(offset);
                else if (lengths.Count < 3 && FilterArgumentParser.TryParseLength(token, out var blur)) lengths.Add(blur);
                else colorTokens.Add(token);
            }
            if (lengths.Count is < 2 or > 3) return false;

            UiColor color;
            if (colorTokens.Count == 0) color = new UiColor(0, 0, 0, 255); // currentColor default
            else if (!UiColor.TryParse(string.Join(' ', colorTokens), out color)) return false;

            function = new CssFilterFunction(Name, [lengths[0], lengths[1], lengths.Count > 2 ? lengths[2] : 0], color);
            return true;
        }
    }

    /// <summary>grayscale(amount): desaturates toward luminance; amount is clamped to [0, 1].</summary>
    private sealed class GrayscaleFilter : FilterFunctionDefinition
    {
        public GrayscaleFilter() : base("grayscale") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [Math.Clamp(amount, 0, 1)]);
            return true;
        }
    }

    /// <summary>hue-rotate(angle): rotates the hue (deg/grad/rad/turn accepted).</summary>
    private sealed class HueRotateFilter : FilterFunctionDefinition
    {
        public HueRotateFilter() : base("hue-rotate") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAngleDegrees(arguments, out var degrees)) return false;
            function = new CssFilterFunction(Name, [degrees]);
            return true;
        }
    }

    /// <summary>invert(amount): flips colors; amount is clamped to [0, 1].</summary>
    private sealed class InvertFilter : FilterFunctionDefinition
    {
        public InvertFilter() : base("invert") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [Math.Clamp(amount, 0, 1)]);
            return true;
        }
    }

    /// <summary>opacity(amount): scales the alpha channel; amount is clamped to [0, 1].</summary>
    private sealed class OpacityFilter : FilterFunctionDefinition
    {
        public OpacityFilter() : base("opacity") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [Math.Clamp(amount, 0, 1)]);
            return true;
        }
    }

    /// <summary>saturate(amount): scales colorfulness (values over 100% allowed).</summary>
    private sealed class SaturateFilter : FilterFunctionDefinition
    {
        public SaturateFilter() : base("saturate") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [amount]);
            return true;
        }
    }

    /// <summary>sepia(amount): applies a sepia tone; amount is clamped to [0, 1].</summary>
    private sealed class SepiaFilter : FilterFunctionDefinition
    {
        public SepiaFilter() : base("sepia") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [Math.Clamp(amount, 0, 1)]);
            return true;
        }
    }

    /// <summary>Argument parsers shared by the built-in filter functions.</summary>
    private static class FilterArgumentParser
    {
        /// <summary>Parses a number or percentage into a fraction (100% =&gt; 1), clamped to zero.</summary>
        public static bool TryParseAmount(string value, out float result)
        {
            result = 0;
            var trimmed = value.Trim();
            var isPercent = trimmed.EndsWith('%');
            if (isPercent) trimmed = trimmed[..^1].Trim();
            if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
            result = Math.Max(0, isPercent ? number / 100f : number);
            return true;
        }

        /// <summary>Parses a CSS length (px or unitless) in pixels without clamping (drop-shadow offsets).</summary>
        public static bool TryParseSignedLength(string value, out float result)
        {
            result = 0;
            var trimmed = value.Trim();
            if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^2];
            return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        /// <summary>Parses a CSS length (px or unitless) in pixels, clamped to zero.</summary>
        public static bool TryParseLength(string value, out float result)
        {
            result = 0;
            var trimmed = value.Trim();
            if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^2];
            if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
            result = Math.Max(0, number);
            return true;
        }

        /// <summary>Parses a CSS angle (deg/grad/rad/turn) into degrees.</summary>
        public static bool TryParseAngleDegrees(string value, out float degrees)
        {
            degrees = 0;
            var trimmed = value.Trim().ToLowerInvariant();
            float multiplier = 1;
            if (trimmed.EndsWith("deg", StringComparison.Ordinal)) trimmed = trimmed[..^3];
            else if (trimmed.EndsWith("grad", StringComparison.Ordinal)) { multiplier = 0.9f; trimmed = trimmed[..^4]; }
            else if (trimmed.EndsWith("rad", StringComparison.Ordinal)) { multiplier = 180f / MathF.PI; trimmed = trimmed[..^3]; }
            else if (trimmed.EndsWith("turn", StringComparison.Ordinal)) { multiplier = 360f; trimmed = trimmed[..^4]; }
            if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
            degrees = number * multiplier;
            return true;
        }

        /// <summary>
        /// Splits a string on whitespace, ignoring whitespace nested inside
        /// parentheses so colors like <c>rgb(0 0 0 / 50%)</c> stay intact.
        /// </summary>
        public static List<string> SplitTopLevel(string value)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            var depth = 0;
            foreach (var ch in value)
            {
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                if (char.IsWhiteSpace(ch) && depth == 0)
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                }
                else current.Append(ch);
            }
            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }
    }
}
