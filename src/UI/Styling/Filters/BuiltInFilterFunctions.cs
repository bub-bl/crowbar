using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Crowbar.UI;

/// <summary>
/// The standard CSS filter functions (<c>blur</c>, <c>brightness</c>,
/// <c>contrast</c>, <c>drop-shadow</c>, <c>grayscale</c>, <c>hue-rotate</c>,
/// <c>invert</c>, <c>opacity</c>, <c>saturate</c>, <c>sepia</c>) registered at
/// startup. They are ordinary <see cref="FilterFunctionDefinition"/>s, so a
/// custom filter can be added the same way by calling
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

    // The color-matrix coefficients below are the canonical matrices published
    // by the CSS Filter Effects specification (grayscale, sepia, saturate,
    // hue-rotate, invert, brightness, contrast, opacity).
    private static SKImageFilter CreateColorMatrixFilter(float[] matrix, SKImageFilter? input)
    {
        using var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
        return SKImageFilter.CreateColorFilter(colorFilter, input);
    }

    private static float[] SaturationMatrix(float s) =>
    [
        0.2126f + 0.7874f * s, 0.7152f - 0.7152f * s, 0.0722f - 0.0722f * s, 0, 0,
        0.2126f - 0.2126f * s, 0.7152f + 0.2848f * s, 0.0722f - 0.0722f * s, 0, 0,
        0.2126f - 0.2126f * s, 0.7152f - 0.7152f * s, 0.0722f + 0.9278f * s, 0, 0,
        0, 0, 0, 1, 0,
    ];

    private static float[] SepiaMatrix(float s) =>
    [
        0.393f + 0.607f * s, 0.769f - 0.769f * s, 0.189f - 0.189f * s, 0, 0,
        0.349f - 0.349f * s, 0.686f + 0.314f * s, 0.168f - 0.168f * s, 0, 0,
        0.272f - 0.272f * s, 0.534f - 0.534f * s, 0.131f + 0.869f * s, 0, 0,
        0, 0, 0, 1, 0,
    ];

    private static float[] InvertMatrix(float amount)
    {
        var diagonal = 1 - 2 * amount;
        var offset = amount;
        return
        [
            diagonal, 0, 0, 0, offset,
            0, diagonal, 0, 0, offset,
            0, 0, diagonal, 0, offset,
            0, 0, 0, 1, 0,
        ];
    }

    private static float[] HueRotateMatrix(float degrees)
    {
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return
        [
            0.213f + cos * 0.787f - sin * 0.213f, 0.715f - cos * 0.715f - sin * 0.715f, 0.072f - cos * 0.072f + sin * 0.928f, 0, 0,
            0.213f - cos * 0.213f + sin * 0.143f, 0.715f + cos * 0.285f + sin * 0.140f, 0.072f - cos * 0.072f - sin * 0.283f, 0, 0,
            0.213f - cos * 0.213f - sin * 0.787f, 0.715f - cos * 0.715f + sin * 0.715f, 0.072f + cos * 0.928f + sin * 0.072f, 0, 0,
            0, 0, 0, 1, 0,
        ];
    }

    private static float[] OpacityMatrix(float amount) =>
    [
        1, 0, 0, 0, 0,
        0, 1, 0, 0, 0,
        0, 0, 1, 0, 0,
        0, 0, 0, amount, 0,
    ];

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

        public override SKImageFilter? CreateImageFilter(CssFilterFunction function, SKImageFilter? input) =>
            SKImageFilter.CreateBlur(function.Parameters[0], function.Parameters[0], SKShaderTileMode.Decal, input);
    }

    /// <summary>brightness(amount): scales the RGB channels (values over 100% allowed).</summary>
    private sealed class BrightnessFilter : ColorMatrixFilter
    {
        public BrightnessFilter() : base("brightness") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [amount]);
            return true;
        }

        protected override float[] BuildMatrix(float amount) =>
        [
            amount, 0, 0, 0, 0,
            0, amount, 0, 0, 0,
            0, 0, amount, 0, 0,
            0, 0, 0, 1, 0,
        ];
    }

    /// <summary>contrast(amount): scales the deviation from mid-gray.</summary>
    private sealed class ContrastFilter : ColorMatrixFilter
    {
        public ContrastFilter() : base("contrast") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [amount]);
            return true;
        }

        protected override float[] BuildMatrix(float amount)
        {
            var offset = 0.5f * (1 - amount);
            return
            [
                amount, 0, 0, 0, offset,
                0, amount, 0, 0, offset,
                0, 0, amount, 0, offset,
                0, 0, 0, 1, 0,
            ];
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

        public override SKImageFilter? CreateImageFilter(CssFilterFunction function, SKImageFilter? input)
        {
            var color = function.Color!.Value;
            // The CSS blur radius is twice the Gaussian sigma, as in box-shadow.
            var sigma = function.Parameters[2] / 2f;
            return SKImageFilter.CreateDropShadow(
                function.Parameters[0], function.Parameters[1], sigma, sigma,
                new SKColor(color.R, color.G, color.B, color.A), input);
        }
    }

    /// <summary>grayscale(amount): desaturates toward luminance; amount is clamped to [0, 1].</summary>
    private sealed class GrayscaleFilter : ColorMatrixFilter
    {
        public GrayscaleFilter() : base("grayscale") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [Math.Clamp(amount, 0, 1)]);
            return true;
        }

        protected override float[] BuildMatrix(float amount) => SaturationMatrix(1 - amount);
    }

    /// <summary>hue-rotate(angle): rotates the hue (deg/grad/rad/turn accepted).</summary>
    private sealed class HueRotateFilter : ColorMatrixFilter
    {
        public HueRotateFilter() : base("hue-rotate") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAngleDegrees(arguments, out var degrees)) return false;
            function = new CssFilterFunction(Name, [degrees]);
            return true;
        }

        protected override float[] BuildMatrix(float amount) => HueRotateMatrix(amount);
    }

    /// <summary>invert(amount): flips colors; amount is clamped to [0, 1].</summary>
    private sealed class InvertFilter : ColorMatrixFilter
    {
        public InvertFilter() : base("invert") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [Math.Clamp(amount, 0, 1)]);
            return true;
        }

        protected override float[] BuildMatrix(float amount) => InvertMatrix(amount);
    }

    /// <summary>opacity(amount): scales the alpha channel; amount is clamped to [0, 1].</summary>
    private sealed class OpacityFilter : ColorMatrixFilter
    {
        public OpacityFilter() : base("opacity") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [Math.Clamp(amount, 0, 1)]);
            return true;
        }

        protected override float[] BuildMatrix(float amount) => OpacityMatrix(amount);
    }

    /// <summary>saturate(amount): scales colorfulness (values over 100% allowed).</summary>
    private sealed class SaturateFilter : ColorMatrixFilter
    {
        public SaturateFilter() : base("saturate") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [amount]);
            return true;
        }

        protected override float[] BuildMatrix(float amount) => SaturationMatrix(amount);
    }

    /// <summary>sepia(amount): applies a sepia tone; amount is clamped to [0, 1].</summary>
    private sealed class SepiaFilter : ColorMatrixFilter
    {
        public SepiaFilter() : base("sepia") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!FilterArgumentParser.TryParseAmount(arguments, out var amount)) return false;
            function = new CssFilterFunction(Name, [Math.Clamp(amount, 0, 1)]);
            return true;
        }

        protected override float[] BuildMatrix(float amount) => SepiaMatrix(1 - amount);
    }

    /// <summary>Base for the color-matrix filters (brightness, contrast, ...).</summary>
    private abstract class ColorMatrixFilter : FilterFunctionDefinition
    {
        protected ColorMatrixFilter(string name) : base(name) { }

        protected abstract float[] BuildMatrix(float amount);

        public override SKImageFilter? CreateImageFilter(CssFilterFunction function, SKImageFilter? input) =>
            CreateColorMatrixFilter(BuildMatrix(function.Parameters[0]), input);
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
