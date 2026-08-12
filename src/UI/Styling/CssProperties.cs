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
        Action<ComputedStyle, CssLength> setter, bool allowAuto = true, bool allowContent = false,
        bool animatable = false) =>
        new(name, getter, setter, (string value, out CssLength result) =>
            CssValueParsers.TryParseCssLength(value, out result, allowAuto, allowContent),
            CssLength.Undefined, inherited: false, animatable, animatable ? LerpLength : null);

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
            defaultValue, inherited, animatable, animatable ? LerpColor : null);

    // Lerpers return object? (boxing struct results) so a null result can
    // signal "cannot interpolate these two values" (discrete animation) for
    // both struct and class values. Method groups whose return type is a struct
    // cannot convert to a delegate returning object?, hence the wrappers.
    private static object? LerpFloat(float from, float to, float t) => from + (to - from) * t;
    private static object? LerpLength(CssLength from, CssLength to, float t) => CssLength.Lerp(from, to, t);
    private static object? LerpColor(UiColor from, UiColor to, float t) => UiColor.Lerp(from, to, t);
    private static object? LerpOrigin(TransformOrigin from, TransformOrigin to, float t) => TransformOrigin.Lerp(from, to, t);

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
        // Mouse cursor shown while the panel is hovered. Values map to the
        // platform's system cursors (SDL) at the input boundary; unknown
        // values are ignored and fall back to the default arrow.
        Register(Keyword("cursor", s => s.Cursor, (s, v) => s.Cursor = v, "auto",
            "auto", "default", "pointer", "text", "crosshair", "move", "wait", "progress",
            "help", "grab", "grabbing", "not-allowed", "ew-resize", "ns-resize",
            "nesw-resize", "nwse-resize", "col-resize", "row-resize", "all-scroll",
            "zoom-in", "zoom-out"));
        Register(Text("text-align", s => s.TextAlign, (s, v) => s.TextAlign = v, "left", inherited: true));
        Register(Text("vertical-align", s => s.VerticalAlign, (s, v) => s.VerticalAlign = v, "top", inherited: true));

        // Dimensions (all animatable: transitions and keyframes interpolate
        // same-unit lengths, and the layout re-runs each frame).
        Register(Length("width", s => s.Width, (s, v) => s.Width = v, allowContent: true, animatable: true));
        Register(Length("height", s => s.Height, (s, v) => s.Height = v, allowContent: true, animatable: true));
        Register(Length("min-width", s => s.MinWidth, (s, v) => s.MinWidth = v, animatable: true));
        Register(Length("max-width", s => s.MaxWidth, (s, v) => s.MaxWidth = v, animatable: true));
        Register(Length("min-height", s => s.MinHeight, (s, v) => s.MinHeight = v, animatable: true));
        Register(Length("max-height", s => s.MaxHeight, (s, v) => s.MaxHeight = v, animatable: true));

        // Flex.
        Register(Number("flex-grow", s => s.FlexGrow, (s, v) => s.FlexGrow = v, 0));
        Register(Number("flex-shrink", s => s.FlexShrink, (s, v) => s.FlexShrink = v, 1));
        Register(Length("flex-basis", s => s.FlexBasis, (s, v) => s.FlexBasis = v, allowContent: true, animatable: true));
        Register(new FlexCssProperty());
        Register(new AspectRatioCssProperty());
        // opacity multiplies down the tree (group opacity), so it participates
        // in inheritance like color: children must refresh when an ancestor's
        // animation moves it.
        Register(Number("opacity", s => s.Opacity, (s, v) => s.Opacity = v, 1, inherited: true, animatable: true));
        Register(Number("border-radius", s => s.BorderRadius, (s, v) => s.BorderRadius = v, 0, animatable: true,
            parser: CssValueParsers.TryParseLength));
        Register(Number("font-size", s => s.FontSize, (s, v) => s.FontSize = v, 16, inherited: true,
            parser: CssValueParsers.TryParseLength));
        Register(Number("line-height", s => s.LineHeight, (s, v) => s.LineHeight = v, 0, inherited: true,
            parser: CssValueParsers.TryParseLength));

        // Typography: family/weight/tracking/case/decoration/whitespace flow
        // down like color and are applied at measure and paint time by the
        // layout engine and the renderer.
        Register(new FontWeightCssProperty());
        Register(Text("font-family", s => s.FontFamily, (s, v) => s.FontFamily = v, "sans-serif", inherited: true));
        Register(Number("letter-spacing", s => s.LetterSpacing, (s, v) => s.LetterSpacing = v, 0, inherited: true,
            parser: CssValueParsers.TryParseSignedLength));
        // Text/white-space inherit like color; values are validated at paint
        // time (unknown keywords behave as the default), so the free-form Text
        // registration with inherited: true is enough here.
        Register(Text("text-transform", s => s.TextTransform, (s, v) => s.TextTransform = v, "none", inherited: true));
        Register(Text("text-decoration", s => s.TextDecoration, (s, v) => s.TextDecoration = v, "none", inherited: true));
        Register(Text("white-space", s => s.WhiteSpace, (s, v) => s.WhiteSpace = v, "normal", inherited: true));
        Register(Keyword("text-overflow", s => s.TextOverflow, (s, v) => s.TextOverflow = v, "clip", "clip", "ellipsis"));

        // Box model: shorthand + individual sides.
        Register(new MarginCssProperty());
        Register(Length("margin-top", s => s.MarginTop, (s, v) => s.MarginTop = v, animatable: true));
        Register(Length("margin-right", s => s.MarginRight, (s, v) => s.MarginRight = v, animatable: true));
        Register(Length("margin-bottom", s => s.MarginBottom, (s, v) => s.MarginBottom = v, animatable: true));
        Register(Length("margin-left", s => s.MarginLeft, (s, v) => s.MarginLeft = v, animatable: true));
        Register(new PaddingCssProperty());
        Register(Length("padding-top", s => s.PaddingTop, (s, v) => s.PaddingTop = v, allowAuto: false, animatable: true));
        Register(Length("padding-right", s => s.PaddingRight, (s, v) => s.PaddingRight = v, allowAuto: false, animatable: true));
        Register(Length("padding-bottom", s => s.PaddingBottom, (s, v) => s.PaddingBottom = v, allowAuto: false, animatable: true));
        Register(Length("padding-left", s => s.PaddingLeft, (s, v) => s.PaddingLeft = v, allowAuto: false, animatable: true));

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

        // Shadows: box-shadow paints below the box's own background, text-shadow
        // paints below the glyphs (and inherits like color). Lists interpolate
        // when both sides have the same shadow count.
        Register(new ShadowListCssProperty<BoxShadow>("box-shadow",
            s => s.BoxShadows, (s, v) => s.BoxShadows = v, CssValueParsers.TryParseBoxShadows, inherited: false,
            BoxShadow.Lerp));
        Register(new ShadowListCssProperty<TextShadow>("text-shadow",
            s => s.TextShadows, (s, v) => s.TextShadows = v, CssValueParsers.TryParseTextShadows, inherited: true,
            TextShadow.Lerp));

        // Absolute positioning offsets.
        Register(Length("top", s => s.PositionTop, (s, v) => s.PositionTop = v, animatable: true));
        Register(Length("right", s => s.PositionRight, (s, v) => s.PositionRight = v, animatable: true));
        Register(Length("bottom", s => s.PositionBottom, (s, v) => s.PositionBottom = v, animatable: true));
        Register(Length("left", s => s.PositionLeft, (s, v) => s.PositionLeft = v, animatable: true));
        Register(Int("z-index", s => s.ZIndex, (s, v) => s.ZIndex = v, 0));

        // Gap.
        Register(new GapCssProperty());
        Register(Length("row-gap", s => s.RowGap, (s, v) => s.RowGap = v, allowAuto: false, animatable: true));
        Register(Length("column-gap", s => s.ColumnGap, (s, v) => s.ColumnGap = v, allowAuto: false, animatable: true));

        // Transitions: a list of (property, duration, timing, delay) specs set by
        // the shorthand or per-index longhands. Each property transitions on its
        // own clock with the first matching spec.
        Register(new TransitionCssProperty());
        Register(Text("transition-property", s => FirstTransition(s).Property,
            (s, v) => ApplyTransitionLonghand(s, v, static (spec, value) => spec with { Property = value }), "none"));
        Register(Text("transition-duration", s => FirstTransition(s).Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            (s, v) => ApplyTransitionLonghand(s, v, static (spec, value) =>
                CssValueParsers.TryParseTime(value, out var duration) ? spec with { Duration = duration } : null), "0"));
        Register(Text("transition-timing-function", s => FirstTransition(s).TimingFunction,
            (s, v) => ApplyTransitionLonghand(s, v, static (spec, value) =>
                TimingFunctions.IsKeyword(value) ? spec with { TimingFunction = value } : null), "ease"));
        Register(Text("transition-delay", s => FirstTransition(s).Delay.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            (s, v) => ApplyTransitionLonghand(s, v, static (spec, value) =>
                CssValueParsers.TryParseTime(value, out var delay, allowNegative: true) ? spec with { Delay = delay } : null), "0"));

        // Animations: keyframes are registered in the global Keyframes registry
        // (from @keyframes blocks or user code); the shorthand builds the whole
        // list, the longhands override per index.
        Register(Text("animation-name", s => FirstAnimation(s).Name,
            (s, v) => ApplyAnimationLonghand(s, v, static (spec, value) => spec with { Name = value }), "none"));
        Register(Text("animation-duration", s => FirstAnimation(s).Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            (s, v) => ApplyAnimationLonghand(s, v, static (spec, value) =>
                CssValueParsers.TryParseTime(value, out var duration) ? spec with { Duration = duration } : null), "0"));
        Register(Text("animation-timing-function", s => FirstAnimation(s).TimingFunction,
            (s, v) => ApplyAnimationLonghand(s, v, static (spec, value) =>
                TimingFunctions.IsKeyword(value) ? spec with { TimingFunction = value } : null), "ease"));
        Register(Text("animation-iteration-count", s => FirstAnimation(s).IterationCount.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            (s, v) => ApplyAnimationLonghand(s, v, static (spec, value) =>
                TryParseIterationCount(value, out var count) ? spec with { IterationCount = count } : null), "1"));
        Register(Text("animation-direction", s => FirstAnimation(s).Direction,
            (s, v) => ApplyAnimationLonghand(s, v, static (spec, value) =>
                IsOneOf(value, "normal", "reverse", "alternate", "alternate-reverse") ? spec with { Direction = value.ToLowerInvariant() } : null), "normal"));
        Register(Text("animation-delay", s => FirstAnimation(s).Delay.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            (s, v) => ApplyAnimationLonghand(s, v, static (spec, value) =>
                CssValueParsers.TryParseTime(value, out var delay, allowNegative: true) ? spec with { Delay = delay } : null), "0"));
        Register(Text("animation-fill-mode", s => FirstAnimation(s).FillMode,
            (s, v) => ApplyAnimationLonghand(s, v, static (spec, value) =>
                IsOneOf(value, "none", "forwards", "backwards", "both") ? spec with { FillMode = value.ToLowerInvariant() } : null), "none"));
        Register(Text("animation-play-state", s => FirstAnimation(s).PlayState,
            (s, v) => ApplyAnimationLonghand(s, v, static (spec, value) =>
                IsOneOf(value, "running", "paused") ? spec with { PlayState = value.ToLowerInvariant() } : null), "running"));
        Register(new AnimationCssProperty());

        // Transform: a single animatable property backed by an ordered function
        // list, plus its pivot. Paint-only (never affect layout).
        Register(new CssProperty<TransformList>("transform", s => s.Transform, (s, v) => s.Transform = v,
            TransformList.TryParse, TransformList.None, inherited: false, animatable: true, TransformList.Lerp));
        Register(new CssProperty<TransformOrigin>("transform-origin", s => s.TransformOrigin, (s, v) => s.TransformOrigin = v,
            TransformOrigin.TryParse, TransformOrigin.Center, inherited: false, animatable: true, LerpOrigin));

        // Filters: the effect lists are parsed and applied through the
        // CssFilterFunctions registry, which owns every filter function.
        Register(Filter("filter", s => s.Filter, (s, v) => s.Filter = v));
        Register(Filter("backdrop-filter", s => s.BackdropFilter, (s, v) => s.BackdropFilter = v));

        // Background images and object-fit: the image is loaded through the
        // UiImageCache at layout/paint time, so these properties only carry the
        // parsed source and fitting parameters.
        Register(new BackgroundCssProperty());
        Register(new CssProperty<string?>("background-image", s => s.BackgroundImage, (s, v) => s.BackgroundImage = v,
            static (string value, out string? url) => CssValueParsers.TryParseUrl(value, out url),
            null, inherited: false, animatable: false, lerper: null));
        Register(new CssProperty<BackgroundSize>("background-size", s => s.BackgroundSize, (s, v) => s.BackgroundSize = v,
            static (string value, out BackgroundSize result) => CssValueParsers.TryParseBackgroundSize(value, out result),
            BackgroundSize.AutoAuto, inherited: false, animatable: false, lerper: null));
        Register(new CssProperty<CssPosition>("background-position", s => s.BackgroundPosition, (s, v) => s.BackgroundPosition = v,
            static (string value, out CssPosition result) => CssValueParsers.TryParseCssPosition(value, out result),
            CssPosition.TopLeft, inherited: false, animatable: false, lerper: null));
        Register(new CssProperty<CssRepeat>("background-repeat", s => s.BackgroundRepeat, (s, v) => s.BackgroundRepeat = v,
            static (string value, out CssRepeat result) => CssValueParsers.TryParseRepeat(value, out result),
            CssRepeat.Repeat, inherited: false, animatable: false, lerper: null));
        Register(Keyword("object-fit", s => s.ObjectFit, (s, v) => s.ObjectFit = v, "fill",
            "fill", "contain", "cover", "none", "scale-down"));
        Register(new CssProperty<CssPosition>("object-position", s => s.ObjectPosition, (s, v) => s.ObjectPosition = v,
            static (string value, out CssPosition result) => CssValueParsers.TryParseCssPosition(value, out result),
            CssPosition.Center, inherited: false, animatable: false, lerper: null));

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
    /// The <c>font-weight</c> property: a keyword (<c>normal</c>, <c>bold</c>,
    /// <c>bolder</c>, <c>lighter</c>) or an explicit 100-900 weight in steps of
    /// 100. <c>bolder</c>/<c>lighter</c> step the current value by one weight.
    /// </summary>
    private sealed class FontWeightCssProperty : CssProperty
    {
        public FontWeightCssProperty() : base("font-weight", inherited: true)
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var trimmed = rawValue.Trim().ToLowerInvariant();
            int weight;
            switch (trimmed)
            {
                case "normal": weight = 400; break;
                case "bold": weight = 700; break;
                case "bolder": weight = Math.Min(900, style.FontWeight + 100); break;
                case "lighter": weight = Math.Max(100, style.FontWeight - 100); break;
                default:
                    if (!int.TryParse(trimmed, out weight)) return false;
                    break;
            }
            if (weight is < 100 or > 900 || weight % 100 != 0) return false;
            style.FontWeight = weight;
            return true;
        }

        public override object? GetValue(ComputedStyle style) => style.FontWeight;
        public override void SetValue(ComputedStyle style, object? value) => style.FontWeight = (int)value!;
        public override object? DefaultValue => 400;
        public override bool ValuesEqual(object? a, object? b) => (int)a! == (int)b!;
        public override object? Lerp(object? from, object? to, float t) => null;
    }

    /// <summary>
    /// The <c>aspect-ratio</c> property: a ratio given as a plain number
    /// (<c>2</c>) or as <c>w / h</c> (<c>16 / 9</c>), optionally prefixed with
    /// <c>auto</c> — <c>auto</c> prefers the element's intrinsic ratio (an
    /// image's) when one is available, falling back to the declared ratio (or
    /// none) otherwise. The numeric part drives Yoga; the flag is exposed on
    /// the computed style for the layout engine.
    /// </summary>
    private sealed class AspectRatioCssProperty : CssProperty
    {
        public AspectRatioCssProperty() : base("aspect-ratio")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var slash = rawValue.IndexOf('/');
            var numerator = (slash >= 0 ? rawValue[..slash] : rawValue).Trim();
            var denominator = slash >= 0 ? rawValue[(slash + 1)..].Trim() : null;

            var auto = false;
            if (numerator.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
            {
                auto = true;
                numerator = numerator["auto".Length..].Trim();
            }

            float ratio;
            if (numerator.Length == 0)
            {
                // `auto` alone: intrinsic ratio only, no fallback.
                if (denominator is not null) return false;
                ratio = 0;
            }
            else if (denominator is null)
            {
                if (!CssValueParsers.TryParseNumber(numerator, out ratio)) return false;
            }
            else
            {
                if (!CssValueParsers.TryParseNumber(numerator, out var width) ||
                    !CssValueParsers.TryParseNumber(denominator, out var height) || height == 0) return false;
                ratio = width / height;
            }

            style.AspectRatio = ratio;
            style.AspectRatioAuto = auto;
            return true;
        }

        // The value is packed with the auto flag so change detection and
        // equality see `aspect-ratio: auto` and `aspect-ratio: 1` as different.
        public override object? GetValue(ComputedStyle style) => (style.AspectRatio, style.AspectRatioAuto);
        public override void SetValue(ComputedStyle style, object? value)
        {
            var (ratio, auto) = ((float, bool))value!;
            style.AspectRatio = ratio;
            style.AspectRatioAuto = auto;
        }

        public override object? DefaultValue => (0f, false);
        public override bool ValuesEqual(object? a, object? b) =>
            a is (float ra, bool aa) && b is (float rb, bool ab) && Math.Abs(ra - rb) < 0.0001f && aa == ab;
        public override object? Lerp(object? from, object? to, float t) => null;
    }

    /// <summary>
    /// The <c>background</c> shorthand: <c>background: url(...) no-repeat
    /// center / cover #000000</c>. Tokens are routed to the individual
    /// background properties (image, color, repeat, position and — after a
    /// <c>/</c> — size); unknown tokens are ignored, mirroring CSS leniency.
    /// </summary>
    private sealed class BackgroundCssProperty : CompoundCssProperty
    {
        public BackgroundCssProperty() : base("background")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var applied = false;
            var positionTokens = new List<string>();
            var sizeTokens = new List<string>();
            var afterSlash = false;
            // The <color> may appear on either side of the <position> / <size>
            // separator, so the whole declaration is tokenized with the slash
            // tracked per token instead of splitting the raw string.
            foreach (var token in CssValueParsers.SplitWhitespaceTokens(rawValue))
            {
                if (token == "/")
                {
                    afterSlash = true;
                    continue;
                }
                if (CssValueParsers.TryParseUrl(token, out var url))
                {
                    style.BackgroundImage = url;
                    applied = true;
                }
                else if (UiColor.TryParse(token, out var color))
                {
                    style.BackgroundColor = color;
                    applied = true;
                }
                else if (!afterSlash && CssValueParsers.TryParseRepeat(token, out var repeat))
                {
                    style.BackgroundRepeat = repeat;
                    applied = true;
                }
                else if (afterSlash && IsSizeToken(token))
                {
                    sizeTokens.Add(token);
                    applied = true;
                }
                else if (!afterSlash && CssValueParsers.TryParseCssPosition(token, out _))
                {
                    positionTokens.Add(token);
                    applied = true;
                }
            }

            if (positionTokens.Count > 0 &&
                CssValueParsers.TryParseCssPosition(string.Join(' ', positionTokens), out var position))
            {
                style.BackgroundPosition = position;
            }
            if (sizeTokens.Count > 0 &&
                CssValueParsers.TryParseBackgroundSize(string.Join(' ', sizeTokens), out var size))
            {
                style.BackgroundSize = size;
            }
            return applied;
        }

        /// <summary>True when a token can be a <c>background-size</c> component (cover, contain, auto, length, percentage).</summary>
        private static bool IsSizeToken(string token)
        {
            var lower = token.ToLowerInvariant();
            if (lower is "cover" or "contain" or "auto") return true;
            return CssValueParsers.TryParseBackgroundSize(token, out _);
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
            // 1 value: all four sides; 2/3 values: left mirrors right (values[1]).
            style.BorderLeftStyle = values.Length > 3 ? values[3] : values.Length > 1 ? values[1] : values[0];
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
            // 1 value: all four sides; 2/3 values: left mirrors right (values[1]).
            style.BorderLeftColor = values.Length > 3 ? values[3] : values.Length > 1 ? values[1] : values[0];
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

    /// <summary>Parses <c>animation-iteration-count</c>: a number or <c>infinite</c>.</summary>
    private static bool TryParseIterationCount(string value, out float result)
    {
        if (value.Trim().Equals("infinite", StringComparison.OrdinalIgnoreCase))
        {
            result = float.PositiveInfinity;
            return true;
        }
        return CssValueParsers.TryParseNumber(value, out result);
    }

    private static bool IsOneOf(string value, params string[] allowed)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return Array.IndexOf(allowed, normalized) >= 0;
    }

    private static TransitionSpec FirstTransition(ComputedStyle style) =>
        style.Transitions.Length > 0 ? style.Transitions[0] : new TransitionSpec();

    private static AnimationSpec FirstAnimation(ComputedStyle style) =>
        style.Animations.Length > 0 ? style.Animations[0] : new AnimationSpec();

    /// <summary>
    /// Applies an animation longhand to the list per index: comma-separated
    /// values map to the specs at the same index (new specs are created when the
    /// value list is longer, missing components keep their current value). A
    /// single value affects the first spec only.
    /// </summary>
    private static void ApplyAnimationLonghand(ComputedStyle style, string rawValue,
        Func<AnimationSpec, string, AnimationSpec?> mutate)
    {
        var values = CssValueParsers.SplitTopLevel(rawValue, ',');
        var specs = style.Animations.Length == 0 ? new[] { new AnimationSpec() } : style.Animations;
        var result = new AnimationSpec[Math.Max(specs.Length, values.Length)];
        for (var i = 0; i < result.Length; i++)
        {
            var spec = i < specs.Length ? specs[i] : new AnimationSpec();
            result[i] = i < values.Length && mutate(spec, values[i]) is { } updated ? updated : spec;
        }
        style.Animations = result;
    }

    /// <summary>Applies a transition longhand to the list per index (see <see cref="ApplyAnimationLonghand"/>).</summary>
    private static void ApplyTransitionLonghand(ComputedStyle style, string rawValue,
        Func<TransitionSpec, string, TransitionSpec?> mutate)
    {
        var values = CssValueParsers.SplitTopLevel(rawValue, ',');
        var specs = style.Transitions.Length == 0 ? new[] { new TransitionSpec() } : style.Transitions;
        var result = new TransitionSpec[Math.Max(specs.Length, values.Length)];
        for (var i = 0; i < result.Length; i++)
        {
            var spec = i < specs.Length ? specs[i] : new TransitionSpec();
            result[i] = i < values.Length && mutate(spec, values[i]) is { } updated ? updated : spec;
        }
        style.Transitions = result;
    }

    /// <summary>
    /// A shadow-list property (<c>box-shadow</c>, <c>text-shadow</c>). Values are
    /// compared by content so an identical re-parse does not look like a style
    /// change (reference equality would flag every recompute as different).
    /// Lists interpolate shadow by shadow when both sides have the same count.
    /// </summary>
    private sealed class ShadowListCssProperty<T> : CssProperty
    {
        private readonly Func<ComputedStyle, T[]> _getter;
        private readonly Action<ComputedStyle, T[]> _setter;
        private readonly TryParseHandler<T[]> _parser;
        private readonly Func<T, T, float, T> _lerper;

        public ShadowListCssProperty(string name, Func<ComputedStyle, T[]> getter,
            Action<ComputedStyle, T[]> setter, TryParseHandler<T[]> parser, bool inherited, Func<T, T, float, T> lerper)
            : base(name, inherited, animatable: true)
        {
            _getter = getter;
            _setter = setter;
            _parser = parser;
            _lerper = lerper;
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            if (!_parser(rawValue, out var value)) return false;
            _setter(style, value);
            return true;
        }

        public override object? GetValue(ComputedStyle style) => _getter(style);
        public override void SetValue(ComputedStyle style, object? value) => _setter(style, (T[])value!);
        public override object? DefaultValue => Array.Empty<T>();
        public override bool ValuesEqual(object? a, object? b) =>
            a is T[] left && b is T[] right ? left.AsSpan().SequenceEqual(right) : ReferenceEquals(a, b);
        public override object? Lerp(object? from, object? to, float t)
        {
            if (from is not T[] left || to is not T[] right) return null;
            // CSS pads the shorter list with transparent zero shadows so a
            // shadow can fade in/out when the counts differ.
            var count = Math.Max(left.Length, right.Length);
            var result = new T[count];
            for (var i = 0; i < count; i++)
            {
                var a = i < left.Length ? left[i] : default!;
                var b = i < right.Length ? right[i] : default!;
                result[i] = _lerper(a, b, t);
            }
            return result;
        }
    }

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
    }    /// <summary>
    /// The <c>transition</c> shorthand: a comma-separated list of
    /// <c>&lt;property&gt; &lt;duration&gt; &lt;timing-function&gt;? &lt;delay&gt;?</c>
    /// specs, with the timing function and delay in either order after the
    /// duration within each spec. The property may be <c>none</c>, <c>all</c> or
    /// a property name; each spec drives its own properties on its own clock.
    /// </summary>
    private sealed class TransitionCssProperty : CompoundCssProperty
    {
        public TransitionCssProperty() : base("transition")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var entries = CssValueParsers.SplitTopLevel(rawValue, ',');
            if (entries.Length == 0) return false;
            var specs = new List<TransitionSpec>(entries.Length);
            foreach (var entry in entries)
            {
                if (TryParseSpec(entry, out var spec)) specs.Add(spec);
            }
            if (specs.Count == 0) return false;
            style.Transitions = specs.ToArray();
            return true;
        }

        private static bool TryParseSpec(string value, out TransitionSpec spec)
        {
            spec = new TransitionSpec();
            var parts = CssValueParsers.SplitWhitespaceTokens(value);
            if (parts.Length == 0) return false;
            spec = spec with { Property = parts[0] };
            var sawDuration = false;
            var sawDelay = false;
            for (var i = 1; i < parts.Length; i++)
            {
                if (CssValueParsers.TryParseTime(parts[i], out var time, allowNegative: true))
                {
                    if (!sawDuration) { spec = spec with { Duration = time }; sawDuration = true; }
                    else if (!sawDelay) { spec = spec with { Delay = time }; sawDelay = true; }
                }
                else if (TimingFunctions.IsKeyword(parts[i]))
                {
                    spec = spec with { TimingFunction = parts[i] };
                }
            }
            return true;
        }
    }

    /// <summary>
    /// The <c>animation</c> shorthand: a comma-separated list of
    /// <c>&lt;name&gt; &lt;duration&gt; &lt;timing-function&gt;? &lt;delay&gt;?
    /// &lt;iteration-count&gt;? &lt;direction&gt;? &lt;fill-mode&gt;?
    /// &lt;play-state&gt;?</c> specs. Within each spec the name is the token that
    /// matches no descriptor; durations/delays must carry a time unit so bare
    /// numbers read as iteration counts. <c>animation: none</c> clears the list.
    /// </summary>
    private sealed class AnimationCssProperty : CompoundCssProperty
    {
        public AnimationCssProperty() : base("animation")
        {
        }

        public override bool TryApply(ComputedStyle style, string rawValue)
        {
            var entries = CssValueParsers.SplitTopLevel(rawValue, ',');
            if (entries.Length == 0) return false;
            var specs = new List<AnimationSpec>(entries.Length);
            foreach (var entry in entries)
            {
                if (TryParseSpec(entry, out var spec)) specs.Add(spec);
            }
            if (specs.Count == 0) return false;
            style.Animations = specs.ToArray();
            return true;
        }

        private static bool TryParseSpec(string value, out AnimationSpec spec)
        {
            spec = new AnimationSpec();
            var parts = CssValueParsers.SplitWhitespaceTokens(value);
            if (parts.Length == 0) return false;
            var name = "none";
            var nameSet = false;
            var duration = 0f;
            var delay = 0f;
            var sawDuration = false;
            var sawDelay = false;
            var timing = "ease";
            var iteration = 1f;
            var direction = "normal";
            var fillMode = "none";
            var playState = "running";

            foreach (var part in parts)
            {
                var lower = part.ToLowerInvariant();
                if (TimingFunctions.IsKeyword(part)) timing = part;
                else if (lower is "normal" or "reverse" or "alternate" or "alternate-reverse") direction = lower;
                else if (lower is "running" or "paused") playState = lower;
                else if (lower is "forwards" or "backwards" or "both" || (lower == "none" && nameSet)) fillMode = lower;
                else if (TryParseIterationCount(part, out var count)) iteration = count;
                else if (TryParseAnimationTime(part, out var time))
                {
                    if (!sawDuration) { duration = time; sawDuration = true; }
                    else if (!sawDelay) { delay = time; sawDelay = true; }
                }
                else if (!nameSet) { name = part; nameSet = true; }
                // Unknown tokens are ignored, mirroring CSS.
            }

            spec = new AnimationSpec(name, duration, timing, iteration, direction, delay, fillMode, playState);
            return true;
        }

        /// <summary>Times in the shorthand must carry a unit (s/ms), so iteration counts stay numeric.</summary>
        private static bool TryParseAnimationTime(string value, out float result)
        {
            result = 0;
            return value.Trim().EndsWith('s') && CssValueParsers.TryParseTime(value, out result, allowNegative: true);
        }
    }
}



