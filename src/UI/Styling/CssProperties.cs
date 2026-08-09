namespace Crowbar.UI;

/// <summary>
/// Registry of every CSS property the styling engine understands. New
/// properties are added by calling <see cref="Register"/>; once registered they
/// flow through <see cref="StyleSheet"/> cascading, change detection and (when
/// animatable) transition interpolation without any further wiring.
/// </summary>
public static class CssProperties
{
    private static readonly Dictionary<string, CssProperty> Registry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every registered property, in registration order.</summary>
    public static IReadOnlyCollection<CssProperty> All => Registry.Values;

    public static bool TryGet(string name, out CssProperty property) => Registry.TryGetValue(name, out property!);

    /// <summary>Registers a property. Throws when the name is already taken.</summary>
    public static void Register(CssProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (!Registry.TryAdd(property.Name, property))
            throw new InvalidOperationException($"A CSS property named '{property.Name}' is already registered.");
    }

    /// <summary>Applies a raw CSS declaration to the style. Unknown or invalid values are ignored.</summary>
    public static bool TryApply(ComputedStyle style, string name, string value) =>
        Registry.TryGetValue(name, out var property) && property.TryApply(style, value);

    /// <summary>Restores every property of the style to its default value.</summary>
    public static void Reset(ComputedStyle style)
    {
        foreach (var property in Registry.Values) property.SetValue(style, property.DefaultValue);
    }

    /// <summary>Creates a free-form keyword/string property (transition, ...).</summary>
    public static CssProperty<string> Text(string name, Func<ComputedStyle, string> getter,
        Action<ComputedStyle, string> setter, string defaultValue, bool inherited = false) =>
        new(name, getter, setter, static (string value, out string result) =>
        {
            result = value;
            return true;
        }, defaultValue, inherited, animatable: false, lerper: null);

    /// <summary>
    /// Creates a keyword property validated against an allowed set. Values are
    /// normalized to lowercase; unknown values are ignored, mirroring CSS.
    /// </summary>
    public static CssProperty<string> Keyword(string name, Func<ComputedStyle, string> getter,
        Action<ComputedStyle, string> setter, string defaultValue, params string[] allowed) =>
        new(name, getter, setter, (string value, out string result) =>
        {
            result = defaultValue;
            var normalized = value.Trim().ToLowerInvariant();
            if (Array.IndexOf(allowed, normalized) >= 0)
            {
                result = normalized;
                return true;
            }
            return false;
        }, defaultValue, inherited: false, animatable: false, lerper: null);

    /// <summary>Creates a non-nullable numeric property (opacity, font-size, ...).</summary>
    public static CssProperty<float> Number(string name, Func<ComputedStyle, float> getter,
        Action<ComputedStyle, float> setter, float defaultValue, bool inherited = false,
        bool animatable = false, TryParseHandler<float>? parser = null) =>
        new(name, getter, setter, parser ?? CssValueParsers.TryParseNumber, defaultValue, inherited, animatable,
            animatable ? LerpFloat : null);

    /// <summary>Creates a CSS length property (width, margin, flex-basis, ...).</summary>
    public static CssProperty<CssLength> Length(string name, Func<ComputedStyle, CssLength> getter,
        Action<ComputedStyle, CssLength> setter, bool allowAuto = true, bool allowContent = false) =>
        new(name, getter, setter, (string value, out CssLength result) =>
            CssValueParsers.TryParseCssLength(value, out result, allowAuto, allowContent),
            CssLength.Undefined, inherited: false, animatable: false, lerper: null);

    /// <summary>Creates an integer-valued property (z-index, ...).</summary>
    public static CssProperty<int> Int(string name, Func<ComputedStyle, int> getter,
        Action<ComputedStyle, int> setter, int defaultValue) =>
        new(name, getter, setter, static (string value, out int result) => int.TryParse(value.Trim(), out result),
            defaultValue, inherited: false, animatable: false, lerper: null);

    /// <summary>Creates a CSS filter property (filter, backdrop-filter, ...).</summary>
    public static CssProperty<CssFilter> Filter(string name, Func<ComputedStyle, CssFilter> getter,
        Action<ComputedStyle, CssFilter> setter) =>
        new(name, getter, setter, static (string value, out CssFilter result) => CssFilterFunctions.TryParse(value, out result),
            CssFilter.None, inherited: false, animatable: false, lerper: null);

    /// <summary>Creates a color property (background-color, color, ...).</summary>
    public static CssProperty<UiColor> Color(string name, Func<ComputedStyle, UiColor> getter,
        Action<ComputedStyle, UiColor> setter, UiColor defaultValue, bool inherited = false,
        bool animatable = false) =>
        new(name, getter, setter, static (string value, out UiColor result) => UiColor.TryParse(value, out result),
            defaultValue, inherited, animatable, animatable ? UiColor.Lerp : null);

    private static float LerpFloat(float from, float to, float t) => from + (to - from) * t;

    static CssProperties() => RegisterBuiltIns();

    private static void RegisterBuiltIns()
    {
        // Layout keywords.
        Register(Keyword("display", s => s.Display, (s, v) => s.Display = v, "flex", "flex", "none", "contents"));
        Register(Keyword("flex-direction", s => s.FlexDirection, (s, v) => s.FlexDirection = v, "column",
            "row", "row-reverse", "column", "column-reverse"));
        Register(Keyword("flex-wrap", s => s.FlexWrap, (s, v) => s.FlexWrap = v, "nowrap",
            "nowrap", "wrap", "wrap-reverse"));
        Register(Keyword("align-items", s => s.AlignItems, (s, v) => s.AlignItems = v, "stretch", AlignKeywords));
        Register(Keyword("align-content", s => s.AlignContent, (s, v) => s.AlignContent = v, "flex-start", AlignKeywords));
        Register(Keyword("align-self", s => s.AlignSelf, (s, v) => s.AlignSelf = v, "auto", AlignKeywords));
        Register(Keyword("justify-content", s => s.JustifyContent, (s, v) => s.JustifyContent = v, "flex-start", JustifyKeywords));
        Register(Keyword("justify-items", s => s.JustifyItems, (s, v) => s.JustifyItems = v, "stretch", JustifyKeywords));
        Register(Keyword("justify-self", s => s.JustifySelf, (s, v) => s.JustifySelf = v, "auto", JustifyKeywords));
        Register(Keyword("position", s => s.PositionType, (s, v) => s.PositionType = v, "relative",
            "relative", "absolute", "static"));
        Register(Keyword("direction", s => s.Direction, (s, v) => s.Direction = v, "inherit",
            "inherit", "ltr", "rtl"));
        Register(Keyword("overflow", s => s.Overflow, (s, v) => s.Overflow = v, "visible",
            "visible", "hidden", "scroll", "auto", "clip"));
        Register(Keyword("box-sizing", s => s.BoxSizing, (s, v) => s.BoxSizing = v, "border-box",
            "border-box", "content-box"));
        Register(Text("text-align", s => s.TextAlign, (s, v) => s.TextAlign = v, "left", inherited: true));
        Register(Text("vertical-align", s => s.VerticalAlign, (s, v) => s.VerticalAlign = v, "top", inherited: true));
        Register(Text("transition-property", s => s.TransitionProperty, (s, v) => s.TransitionProperty = v, "none"));
        Register(Text("transition-timing-function", s => s.TransitionTimingFunction, (s, v) =>
            s.TransitionTimingFunction = v, "ease"));

        // Dimensions.
        Register(Length("width", s => s.Width, (s, v) => s.Width = v, allowContent: true));
        Register(Length("height", s => s.Height, (s, v) => s.Height = v, allowContent: true));
        Register(Length("min-width", s => s.MinWidth, (s, v) => s.MinWidth = v));
        Register(Length("max-width", s => s.MaxWidth, (s, v) => s.MaxWidth = v));
        Register(Length("min-height", s => s.MinHeight, (s, v) => s.MinHeight = v));
        Register(Length("max-height", s => s.MaxHeight, (s, v) => s.MaxHeight = v));

        // Flex.
        Register(Number("flex-grow", s => s.FlexGrow, (s, v) => s.FlexGrow = v, 0));
        Register(Number("flex-shrink", s => s.FlexShrink, (s, v) => s.FlexShrink = v, 1));
        Register(Length("flex-basis", s => s.FlexBasis, (s, v) => s.FlexBasis = v, allowContent: true));
        Register(new FlexCssProperty());
        Register(Number("aspect-ratio", s => s.AspectRatio, (s, v) => s.AspectRatio = v, 0));
        Register(Number("opacity", s => s.Opacity, (s, v) => s.Opacity = v, 1, animatable: true));
        Register(Number("border-radius", s => s.BorderRadius, (s, v) => s.BorderRadius = v, 0, animatable: true,
            parser: CssValueParsers.TryParseLength));
        Register(Number("font-size", s => s.FontSize, (s, v) => s.FontSize = v, 16, inherited: true,
            parser: CssValueParsers.TryParseLength));
        Register(Number("line-height", s => s.LineHeight, (s, v) => s.LineHeight = v, 0, inherited: true,
            parser: CssValueParsers.TryParseLength));
        Register(Number("transition-duration", s => s.TransitionDuration, (s, v) => s.TransitionDuration = v, 0,
            parser: CssValueParsers.TryParseTime));

        // Box model: shorthand + individual sides.
        Register(new MarginCssProperty());
        Register(Length("margin-top", s => s.MarginTop, (s, v) => s.MarginTop = v));
        Register(Length("margin-right", s => s.MarginRight, (s, v) => s.MarginRight = v));
        Register(Length("margin-bottom", s => s.MarginBottom, (s, v) => s.MarginBottom = v));
        Register(Length("margin-left", s => s.MarginLeft, (s, v) => s.MarginLeft = v));
        Register(new PaddingCssProperty());
        Register(Length("padding-top", s => s.PaddingTop, (s, v) => s.PaddingTop = v, allowAuto: false));
        Register(Length("padding-right", s => s.PaddingRight, (s, v) => s.PaddingRight = v, allowAuto: false));
        Register(Length("padding-bottom", s => s.PaddingBottom, (s, v) => s.PaddingBottom = v, allowAuto: false));
        Register(Length("padding-left", s => s.PaddingLeft, (s, v) => s.PaddingLeft = v, allowAuto: false));

        // Border: the widths participate in the box model through Yoga; the
        // style and color are paint-only. `border`/`border-width`/`border-style`/
        // `border-color` are 1-to-4 value shorthands, plus per-side longhands.
        Register(new BorderCssProperty());
        Register(new BorderWidthCssProperty());
        Register(new BorderStyleCssProperty());
        Register(new BorderColorCssProperty());
        Register(new BorderSideCssProperty("border-top", (s, v) => s.BorderTop = v, (s, v) => s.BorderTopStyle = v, (s, v) => s.BorderTopColor = v));
        Register(new BorderSideCssProperty("border-right", (s, v) => s.BorderRight = v, (s, v) => s.BorderRightStyle = v, (s, v) => s.BorderRightColor = v));
        Register(new BorderSideCssProperty("border-bottom", (s, v) => s.BorderBottom = v, (s, v) => s.BorderBottomStyle = v, (s, v) => s.BorderBottomColor = v));
        Register(new BorderSideCssProperty("border-left", (s, v) => s.BorderLeft = v, (s, v) => s.BorderLeftStyle = v, (s, v) => s.BorderLeftColor = v));
        Register(Length("border-top-width", s => s.BorderTop, (s, v) => s.BorderTop = v, allowAuto: false));
        Register(Length("border-right-width", s => s.BorderRight, (s, v) => s.BorderRight = v, allowAuto: false));
        Register(Length("border-bottom-width", s => s.BorderBottom, (s, v) => s.BorderBottom = v, allowAuto: false));
        Register(Length("border-left-width", s => s.BorderLeft, (s, v) => s.BorderLeft = v, allowAuto: false));
        Register(Keyword("border-top-style", s => s.BorderTopStyle, (s, v) => s.BorderTopStyle = v, "none", BorderStyleKeywords));
        Register(Keyword("border-right-style", s => s.BorderRightStyle, (s, v) => s.BorderRightStyle = v, "none", BorderStyleKeywords));
        Register(Keyword("border-bottom-style", s => s.BorderBottomStyle, (s, v) => s.BorderBottomStyle = v, "none", BorderStyleKeywords));
        Register(Keyword("border-left-style", s => s.BorderLeftStyle, (s, v) => s.BorderLeftStyle = v, "none", BorderStyleKeywords));
        Register(Color("border-top-color", s => s.BorderTopColor, (s, v) => s.BorderTopColor = v, UiColor.Black));
        Register(Color("border-right-color", s => s.BorderRightColor, (s, v) => s.BorderRightColor = v, UiColor.Black));
        Register(Color("border-bottom-color", s => s.BorderBottomColor, (s, v) => s.BorderBottomColor = v, UiColor.Black));
        Register(Color("border-left-color", s => s.BorderLeftColor, (s, v) => s.BorderLeftColor = v, UiColor.Black));

        // Outline: drawn outside the border box, never affects layout.
        Register(new OutlineCssProperty());
        Register(Keyword("outline-style", s => s.OutlineStyle, (s, v) => s.OutlineStyle = v, "none", BorderStyleKeywords));
        Register(Number("outline-width", s => s.OutlineWidth, (s, v) => s.OutlineWidth = v, 0,
            parser: CssValueParsers.TryParseOutlineWidth));
        Register(Number("outline-offset", s => s.OutlineOffset, (s, v) => s.OutlineOffset = v, 0,
            parser: CssValueParsers.TryParseOffsetLength));
        Register(Color("outline-color", s => s.OutlineColor, (s, v) => s.OutlineColor = v, UiColor.Black));

        // Absolute positioning offsets.
        Register(Length("top", s => s.PositionTop, (s, v) => s.PositionTop = v));
        Register(Length("right", s => s.PositionRight, (s, v) => s.PositionRight = v));
        Register(Length("bottom", s => s.PositionBottom, (s, v) => s.PositionBottom = v));
        Register(Length("left", s => s.PositionLeft, (s, v) => s.PositionLeft = v));
        Register(Int("z-index", s => s.ZIndex, (s, v) => s.ZIndex = v, 0));

        // Gap.
        Register(new GapCssProperty());
        Register(Length("row-gap", s => s.RowGap, (s, v) => s.RowGap = v, allowAuto: false));
        Register(Length("column-gap", s => s.ColumnGap, (s, v) => s.ColumnGap = v, allowAuto: false));

        // Transitions.
        Register(new TransitionCssProperty());

        // Filters: the effect lists are parsed and applied through the
        // CssFilterFunctions registry, which owns every filter function.
        Register(Filter("filter", s => s.Filter, (s, v) => s.Filter = v));
        Register(Filter("backdrop-filter", s => s.BackdropFilter, (s, v) => s.BackdropFilter = v));

        // Colors.
        Register(Color("background-color", s => s.BackgroundColor, (s, v) => s.BackgroundColor = v,
            UiColor.Transparent, animatable: true));
        Register(Color("color", s => s.Color, (s, v) => s.Color = v, UiColor.White, inherited: true, animatable: true));

        // Scrollbar styling, applied per element.
        Register(new ScrollbarColorCssProperty());
        Register(Number("scrollbar-width", s => s.ScrollbarWidth, (s, v) => s.ScrollbarWidth = v, 0,
            parser: CssValueParsers.TryParseScrollbarWidth));
        Register(Number("scrollbar-radius", s => s.ScrollbarRadius, (s, v) => s.ScrollbarRadius = v, 5,
            animatable: true, parser: CssValueParsers.TryParseLength));
    }

    private static readonly string[] AlignKeywords = ["auto", "flex-start", "flex-end", "center", "stretch", "baseline", "space-between", "space-around", "space-evenly", "start", "end"];
    private static readonly string[] JustifyKeywords = ["auto", "flex-start", "flex-end", "center", "stretch", "space-between", "space-around", "space-evenly", "start", "end"];
    private static readonly string[] BorderStyleKeywords = ["none", "hidden", "solid", "dashed", "dotted", "double", "groove", "ridge", "inset", "outset"];

    private sealed class MarginCssProperty : CompoundCssProperty
    {
        public MarginCssProperty() : base("margin")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            if (!CssValueParsers.TryParseLengthBox(rawValue, out var box, allowAuto: true)) return false;
            style.Margin = box.Top;
            style.MarginTop = box.Top;
            style.MarginRight = box.Right;
            style.MarginBottom = box.Bottom;
            style.MarginLeft = box.Left;
            return true;
        }
    }

    private sealed class PaddingCssProperty : CompoundCssProperty
    {
        public PaddingCssProperty() : base("padding")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            if (!CssValueParsers.TryParseLengthBox(rawValue, out var box)) return false;
            style.Padding = box.Top;
            style.PaddingTop = box.Top;
            style.PaddingRight = box.Right;
            style.PaddingBottom = box.Bottom;
            style.PaddingLeft = box.Left;
            return true;
        }
    }

    private sealed class GapCssProperty : CompoundCssProperty
    {
        public GapCssProperty() : base("gap")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var parts = rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2) return false;
            if (!CssValueParsers.TryParseCssLength(parts[0], out var row, allowAuto: false, allowContent: false)) return false;
            var column = parts.Length > 1 &&
                         CssValueParsers.TryParseCssLength(parts[1], out var parsedColumn, allowAuto: false, allowContent: false)
                ? parsedColumn
                : row;
            style.Gap = row;
            style.RowGap = row;
            style.ColumnGap = column;
            return true;
        }
    }

    /// <summary>
    /// The <c>flex</c> shorthand expands into flex-grow/flex-shrink/flex-basis
    /// per the CSS spec: <c>flex: 1</c> means <c>1 1 0%</c>, <c>flex: none</c>
    /// means <c>0 0 auto</c>.
    /// </summary>
    private sealed class FlexCssProperty : CompoundCssProperty
    {
        public FlexCssProperty() : base("flex")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var parts = rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts.Length > 3) return false;
            if (parts.Length == 1 && parts[0].Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                style.FlexGrow = 0;
                style.FlexShrink = 0;
                style.FlexBasis = CssLength.Auto;
                return true;
            }

            float grow;
            float shrink;
            CssLength basis;
            if (parts.Length == 1)
            {
                if (CssValueParsers.TryParseNumber(parts[0], out grow))
                {
                    shrink = 1;
                    basis = CssLength.Points(0);
                }
                else if (CssValueParsers.TryParseCssLength(parts[0], out basis))
                {
                    grow = 1;
                    shrink = 1;
                }
                else return false;
            }
            else if (parts.Length == 2)
            {
                if (CssValueParsers.TryParseNumber(parts[0], out grow) && CssValueParsers.TryParseNumber(parts[1], out shrink))
                {
                    basis = CssLength.Auto;
                }
                else if (CssValueParsers.TryParseNumber(parts[0], out grow) &&
                         CssValueParsers.TryParseCssLength(parts[1], out basis))
                {
                    shrink = 1;
                }
                else return false;
            }
            else
            {
                if (!CssValueParsers.TryParseNumber(parts[0], out grow) ||
                    !CssValueParsers.TryParseNumber(parts[1], out shrink) ||
                    !CssValueParsers.TryParseCssLength(parts[2], out basis)) return false;
            }

            style.FlexGrow = Math.Max(0, grow);
            style.FlexShrink = Math.Max(0, shrink);
            style.FlexBasis = basis;
            return true;
        }
    }

    /// <summary>
    /// The <c>border</c> shorthand: width, style and color in any order
    /// (<c>1px solid #ccc</c>, <c>solid</c>, <c>2px dashed</c>...), applied to
    /// all four sides. At least one valid component is required, mirroring CSS
    /// (missing parts keep their current value).
    /// </summary>
    private sealed class BorderCssProperty : CompoundCssProperty
    {
        public BorderCssProperty() : base("border")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var applied = false;
            foreach (var token in rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (CssValueParsers.TryParseCssLength(token, out var length, allowAuto: false, allowContent: false) && length.IsDefined)
                {
                    style.Border = length;
                    style.BorderTop = style.BorderRight = style.BorderBottom = style.BorderLeft = length;
                    applied = true;
                }
                else if (IsBorderStyle(token))
                {
                    style.BorderTopStyle = style.BorderRightStyle = style.BorderBottomStyle = style.BorderLeftStyle = token.ToLowerInvariant();
                    applied = true;
                }
                else if (UiColor.TryParse(token, out var color))
                {
                    style.BorderTopColor = style.BorderRightColor = style.BorderBottomColor = style.BorderLeftColor = color;
                    applied = true;
                }
            }
            return applied;
        }
    }

    /// <summary>
    /// The per-side <c>border-top/-right/-bottom/-left</c> shorthand: width,
    /// style and color in any order, applied to that single side.
    /// </summary>
    private sealed class BorderSideCssProperty : CompoundCssProperty
    {
        private readonly Action<ComputedStyle, CssLength> _setWidth;
        private readonly Action<ComputedStyle, string> _setStyle;
        private readonly Action<ComputedStyle, UiColor> _setColor;

        public BorderSideCssProperty(string name, Action<ComputedStyle, CssLength> setWidth,
            Action<ComputedStyle, string> setStyle, Action<ComputedStyle, UiColor> setColor) : base(name)
        {
            _setWidth = setWidth;
            _setStyle = setStyle;
            _setColor = setColor;
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var applied = false;
            foreach (var token in rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (CssValueParsers.TryParseCssLength(token, out var length, allowAuto: false, allowContent: false) && length.IsDefined)
                {
                    _setWidth(style, length);
                    applied = true;
                }
                else if (IsBorderStyle(token))
                {
                    _setStyle(style, token.ToLowerInvariant());
                    applied = true;
                }
                else if (UiColor.TryParse(token, out var color))
                {
                    _setColor(style, color);
                    applied = true;
                }
            }
            return applied;
        }
    }

    /// <summary>The <c>border-width</c> shorthand: 1 to 4 lengths applied to each side.</summary>
    private sealed class BorderWidthCssProperty : CompoundCssProperty
    {
        public BorderWidthCssProperty() : base("border-width")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            if (!CssValueParsers.TryParseLengthBox(rawValue, out var box)) return false;
            style.Border = box.Top;
            style.BorderTop = box.Top;
            style.BorderRight = box.Right;
            style.BorderBottom = box.Bottom;
            style.BorderLeft = box.Left;
            return true;
        }
    }

    /// <summary>The <c>border-style</c> shorthand: 1 to 4 keywords applied to each side.</summary>
    private sealed class BorderStyleCssProperty : CompoundCssProperty
    {
        public BorderStyleCssProperty() : base("border-style")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var parts = rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 4) return false;
            var values = new string[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!IsBorderStyle(parts[i])) return false;
                values[i] = parts[i].ToLowerInvariant();
            }
            style.BorderTopStyle = values[0];
            style.BorderRightStyle = values.Length > 1 ? values[1] : values[0];
            style.BorderBottomStyle = values.Length > 2 ? values[2] : values[0];
            style.BorderLeftStyle = values.Length > 3 ? values[3] : values[1];
            return true;
        }
    }

    /// <summary>The <c>border-color</c> shorthand: 1 to 4 colors applied to each side.</summary>
    private sealed class BorderColorCssProperty : CompoundCssProperty
    {
        public BorderColorCssProperty() : base("border-color")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var parts = rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 4) return false;
            var values = new UiColor[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!UiColor.TryParse(parts[i], out values[i])) return false;
            }
            style.BorderTopColor = values[0];
            style.BorderRightColor = values.Length > 1 ? values[1] : values[0];
            style.BorderBottomColor = values.Length > 2 ? values[2] : values[0];
            style.BorderLeftColor = values.Length > 3 ? values[3] : values[1];
            return true;
        }
    }

    /// <summary>
    /// The <c>outline</c> shorthand: width, style and color in any order. The
    /// outline is painted outside the border box and never affects layout.
    /// </summary>
    private sealed class OutlineCssProperty : CompoundCssProperty
    {
        public OutlineCssProperty() : base("outline")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var applied = false;
            foreach (var token in rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (CssValueParsers.TryParseOutlineWidth(token, out var width))
                {
                    style.OutlineWidth = width;
                    applied = true;
                }
                else if (IsBorderStyle(token))
                {
                    style.OutlineStyle = token.ToLowerInvariant();
                    applied = true;
                }
                else if (UiColor.TryParse(token, out var color))
                {
                    style.OutlineColor = color;
                    applied = true;
                }
            }
            return applied;
        }
    }

    private static bool IsBorderStyle(string token) =>
        Array.IndexOf(BorderStyleKeywords, token.ToLowerInvariant()) >= 0;

    /// <summary>
    /// The <c>scrollbar-color</c> shorthand: <c>auto</c> resets to the engine
    /// defaults, otherwise two colors (thumb then track) as in CSS.
    /// </summary>
    private sealed class ScrollbarColorCssProperty : CompoundCssProperty
    {
        public ScrollbarColorCssProperty() : base("scrollbar-color")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var parts = rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 1 && parts[0].Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                style.ScrollbarThumbColor = new UiColor(150, 172, 205, 215);
                style.ScrollbarTrackColor = new UiColor(15, 24, 40, 110);
                return true;
            }
            if (parts.Length != 2) return false;
            if (!UiColor.TryParse(parts[0], out var thumb) || !UiColor.TryParse(parts[1], out var track)) return false;
            style.ScrollbarThumbColor = thumb;
            style.ScrollbarTrackColor = track;
            return true;
        }
    }

    private sealed class TransitionCssProperty : CompoundCssProperty
    {
        public TransitionCssProperty() : base("transition")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var parts = rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return false;
            style.TransitionProperty = parts[0];
            if (parts.Length > 1 && CssValueParsers.TryParseTime(parts[1], out var duration))
                style.TransitionDuration = duration;
            if (parts.Length > 2) style.TransitionTimingFunction = parts[2];
            return true;
        }
    }
}
