namespace Crowbar.UI;

/// <summary>
/// The fully computed style of a panel after the CSS cascade, inheritance and
/// transitions have been applied. Values are plain CLR properties; the
/// <see cref="CssProperties"/> registry owns parsing, equality and animation
/// for every property. Layout lengths carry their unit (<see cref="CssLength"/>)
/// so Yoga.Net can resolve percentages, auto margins and content-based sizes.
/// </summary>
public sealed class ComputedStyle
{
    public string Display { get; set; } = "flex";
    public string FlexDirection { get; set; } = "column";
    public string FlexWrap { get; set; } = "nowrap";
    public string AlignItems { get; set; } = "stretch";
    public string AlignContent { get; set; } = "flex-start";
    public string AlignSelf { get; set; } = "auto";
    public string JustifyContent { get; set; } = "flex-start";
    public string JustifyItems { get; set; } = "stretch";
    public string JustifySelf { get; set; } = "auto";
    public string PositionType { get; set; } = "relative";
    public string Direction { get; set; } = "inherit";
    public string Overflow { get; set; } = "visible";
    public string TextAlign { get; set; } = "left";
    public string VerticalAlign { get; set; } = "top";
    public string BoxSizing { get; set; } = "border-box";
    /// <summary>Mouse cursor shown while the panel is hovered (<c>cursor</c> property).</summary>
    public string Cursor { get; set; } = "auto";

    public CssLength Width { get; set; }
    public CssLength Height { get; set; }
    public CssLength MinWidth { get; set; }
    public CssLength MaxWidth { get; set; }
    public CssLength MinHeight { get; set; }
    public CssLength MaxHeight { get; set; }
    public CssLength FlexBasis { get; set; }
    public float FlexGrow { get; set; }
    public float FlexShrink { get; set; } = 1;
    public float AspectRatio { get; set; }

    public CssLength Gap { get; set; }
    public CssLength RowGap { get; set; }
    public CssLength ColumnGap { get; set; }

    public CssLength Margin { get; set; }
    public CssLength MarginTop { get; set; }
    public CssLength MarginRight { get; set; }
    public CssLength MarginBottom { get; set; }
    public CssLength MarginLeft { get; set; }

    public CssLength Padding { get; set; }
    public CssLength PaddingTop { get; set; }
    public CssLength PaddingRight { get; set; }
    public CssLength PaddingBottom { get; set; }
    public CssLength PaddingLeft { get; set; }

    public CssLength Border { get; set; }
    public CssLength BorderTop { get; set; }
    public CssLength BorderRight { get; set; }
    public CssLength BorderBottom { get; set; }
    public CssLength BorderLeft { get; set; }

    /// <summary>Border line style per side (<c>none</c>, <c>solid</c>, <c>dashed</c>, <c>dotted</c>, ...).</summary>
    public string BorderTopStyle { get; set; } = "none";
    public string BorderRightStyle { get; set; } = "none";
    public string BorderBottomStyle { get; set; } = "none";
    public string BorderLeftStyle { get; set; } = "none";
    /// <summary>Border color per side (initial value: black, like CSS <c>currentColor</c> at its default).</summary>
    public UiColor BorderTopColor { get; set; } = UiColor.Black;
    public UiColor BorderRightColor { get; set; } = UiColor.Black;
    public UiColor BorderBottomColor { get; set; } = UiColor.Black;
    public UiColor BorderLeftColor { get; set; } = UiColor.Black;

    /// <summary>Outline line style (uniform around the box; <c>outline</c> does not affect layout).</summary>
    public string OutlineStyle { get; set; } = "none";
    /// <summary>Outline thickness in px (initial value: <c>medium</c> = 3px).</summary>
    public float OutlineWidth { get; set; }
    /// <summary>Distance between the border box edge and the outline, in px (may be negative).</summary>
    public float OutlineOffset { get; set; }
    public UiColor OutlineColor { get; set; } = UiColor.Black;

    public CssLength PositionTop { get; set; }
    public CssLength PositionRight { get; set; }
    public CssLength PositionBottom { get; set; }
    public CssLength PositionLeft { get; set; }
    /// <summary>Stacking order among siblings (CSS <c>z-index</c>). Higher values paint above.</summary>
    public int ZIndex { get; set; }

    /// <summary>Scrollbar thickness in px; 0 means <c>auto</c> (the engine default).</summary>
    public float ScrollbarWidth { get; set; }
    /// <summary>Corner radius of the scrollbar track and thumb, in px.</summary>
    public float ScrollbarRadius { get; set; } = 5;
    public UiColor ScrollbarThumbColor { get; set; } = new(150, 172, 205, 215);
    public UiColor ScrollbarTrackColor { get; set; } = new(15, 24, 40, 110);

    public float Opacity { get; set; } = 1;
    /// <summary>The CSS <c>box-shadow</c> list, painted below the background (first shadow on top).</summary>
    public BoxShadow[] BoxShadows { get; set; } = [];
    /// <summary>The CSS <c>text-shadow</c> list, painted below the text glyphs (first shadow on top).</summary>
    public TextShadow[] TextShadows { get; set; } = [];
    /// <summary>The CSS <c>filter</c> list applied to the panel's own rendering (background, text, children).</summary>
    public CssFilter Filter { get; set; } = CssFilter.None;
    /// <summary>The CSS <c>backdrop-filter</c> list applied to the pixels painted behind the panel.</summary>
    public CssFilter BackdropFilter { get; set; } = CssFilter.None;
    public float BorderRadius { get; set; }
    public float FontSize { get; set; } = 16;
    public float LineHeight { get; set; }

    /// <summary>The CSS <c>transition</c> list (comma-separated specs, each with its own property and timing).</summary>
    public TransitionSpec[] Transitions { get; set; } = TransitionSpec.None;

    /// <summary>The CSS <c>animation</c> list (comma-separated specs, each with its own keyframe name and timing).</summary>
    public AnimationSpec[] Animations { get; set; } = [];

    /// <summary>The CSS <c>transform</c>: an ordered list of functions (paint-only, never affects layout).</summary>
    public TransformList Transform { get; set; } = TransformList.None;
    /// <summary>The CSS <c>transform-origin</c> pivot, resolved against the element box (default: center).</summary>
    public TransformOrigin TransformOrigin { get; set; } = TransformOrigin.Center;

    /// <summary>True when the panel carries a non-identity transform.</summary>
    public bool HasTransform => !Transform.IsNone && !Transform.IsIdentity;

    public UiColor BackgroundColor { get; set; } = UiColor.Transparent;
    /// <summary>The CSS <c>background-image</c> source (<c>url(...)</c>), or null for <c>none</c>.</summary>
    public string? BackgroundImage { get; set; }
    /// <summary>The CSS <c>background-size</c> (auto, cover, contain or explicit lengths).</summary>
    public BackgroundSize BackgroundSize { get; set; } = BackgroundSize.AutoAuto;
    /// <summary>The CSS <c>background-position</c> (initial: <c>0% 0%</c>).</summary>
    public CssPosition BackgroundPosition { get; set; } = CssPosition.TopLeft;
    /// <summary>The CSS <c>background-repeat</c> per axis (initial: <c>repeat</c>).</summary>
    public CssRepeat BackgroundRepeat { get; set; } = CssRepeat.Repeat;
    /// <summary>The CSS <c>object-fit</c> of an <c>&lt;img&gt;</c> panel (fill, contain, cover, none, scale-down).</summary>
    public string ObjectFit { get; set; } = "fill";
    /// <summary>The CSS <c>object-position</c> (initial: <c>50% 50%</c>).</summary>
    public CssPosition ObjectPosition { get; set; } = CssPosition.Center;
    /// <summary>
    /// True when <c>aspect-ratio</c> was declared with the <c>auto</c> keyword:
    /// the element's intrinsic ratio (an image's) wins when one is available.
    /// </summary>
    public bool AspectRatioAuto { get; set; }
    public UiColor Color { get; set; } = UiColor.White;
    /// <summary>The CSS <c>font-family</c> (first supported family wins at paint time).</summary>
    public string FontFamily { get; set; } = "sans-serif";
    /// <summary>The CSS <c>font-weight</c> (100-900; <c>bold</c> is 700).</summary>
    public int FontWeight { get; set; } = 400;
    /// <summary>The CSS <c>letter-spacing</c> in px (tracking between glyphs).</summary>
    public float LetterSpacing { get; set; }
    /// <summary>The CSS <c>text-transform</c> (none, uppercase, lowercase, capitalize).</summary>
    public string TextTransform { get; set; } = "none";
    /// <summary>The CSS <c>text-decoration</c> (none, underline, line-through, overline, space-separated).</summary>
    public string TextDecoration { get; set; } = "none";
    /// <summary>The CSS <c>white-space</c> (normal, nowrap, pre, pre-wrap, pre-line).</summary>
    public string WhiteSpace { get; set; } = "normal";
    /// <summary>The CSS <c>text-overflow</c> (clip or ellipsis).</summary>
    public string TextOverflow { get; set; } = "clip";

    /// <summary>
    /// Bit per registered CSS property that the cascade wrote since the last
    /// <see cref="ResetToDefaults"/>. Change detection compares only the
    /// properties in the union of the two styles' masks: an unwritten property
    /// holds its default on both sides, so comparing it is pure waste. The
    /// invariant is maintained at every write path (TryApply, CopyFrom);
    /// keyframe/transition writes go to composed clones and are irrelevant to
    /// the resting-vs-cascade comparison that uses the mask.
    /// </summary>
    private System.UInt128 _writtenMask;

    internal System.UInt128 WrittenMask => _writtenMask;

    internal static System.UInt128 BitOf(CssProperty property) => (System.UInt128)1 << property.Index;

    /// <summary>Records that <paramref name="property"/> was just applied to this style.</summary>
    internal void MarkWritten(CssProperty property) => _writtenMask |= property.WrittenBits;

    /// <summary>Records that every property in <paramref name="bits"/> was just applied to this style.</summary>
    internal void MarkWritten(System.UInt128 bits) => _writtenMask |= bits;

    public ComputedStyle Clone() => (ComputedStyle)MemberwiseClone();

    /// <summary>
    /// Immutable template holding every default value. The cascade writes the
    /// per-panel compute buffer back to defaults through
    /// <see cref="ResetToDefaults"/> before re-applying rules, so a reused
    /// style never leaks a value from the previous pass. Never mutated.
    /// </summary>
    private static readonly ComputedStyle DefaultTemplate = new();

    /// <summary>
    /// Restores every property to its default value without allocating. The
    /// alternative (a fresh <c>new ComputedStyle()</c> per panel per cascade
    /// pass) was the dominant per-pass allocation in the profiler; the compute
    /// buffer is reused and reset in place instead.
    /// </summary>
    internal void ResetToDefaults() => CopyFrom(DefaultTemplate);

    private void CopyFrom(ComputedStyle other)
    {
        Display = other.Display;
        FlexDirection = other.FlexDirection;
        FlexWrap = other.FlexWrap;
        AlignItems = other.AlignItems;
        AlignContent = other.AlignContent;
        AlignSelf = other.AlignSelf;
        JustifyContent = other.JustifyContent;
        JustifyItems = other.JustifyItems;
        JustifySelf = other.JustifySelf;
        PositionType = other.PositionType;
        Direction = other.Direction;
        Overflow = other.Overflow;
        TextAlign = other.TextAlign;
        VerticalAlign = other.VerticalAlign;
        BoxSizing = other.BoxSizing;
        Cursor = other.Cursor;
        Width = other.Width;
        Height = other.Height;
        MinWidth = other.MinWidth;
        MaxWidth = other.MaxWidth;
        MinHeight = other.MinHeight;
        MaxHeight = other.MaxHeight;
        FlexBasis = other.FlexBasis;
        FlexGrow = other.FlexGrow;
        FlexShrink = other.FlexShrink;
        AspectRatio = other.AspectRatio;
        Gap = other.Gap;
        RowGap = other.RowGap;
        ColumnGap = other.ColumnGap;
        Margin = other.Margin;
        MarginTop = other.MarginTop;
        MarginRight = other.MarginRight;
        MarginBottom = other.MarginBottom;
        MarginLeft = other.MarginLeft;
        Padding = other.Padding;
        PaddingTop = other.PaddingTop;
        PaddingRight = other.PaddingRight;
        PaddingBottom = other.PaddingBottom;
        PaddingLeft = other.PaddingLeft;
        Border = other.Border;
        BorderTop = other.BorderTop;
        BorderRight = other.BorderRight;
        BorderBottom = other.BorderBottom;
        BorderLeft = other.BorderLeft;
        BorderTopStyle = other.BorderTopStyle;
        BorderRightStyle = other.BorderRightStyle;
        BorderBottomStyle = other.BorderBottomStyle;
        BorderLeftStyle = other.BorderLeftStyle;
        BorderTopColor = other.BorderTopColor;
        BorderRightColor = other.BorderRightColor;
        BorderBottomColor = other.BorderBottomColor;
        BorderLeftColor = other.BorderLeftColor;
        OutlineStyle = other.OutlineStyle;
        OutlineWidth = other.OutlineWidth;
        OutlineOffset = other.OutlineOffset;
        OutlineColor = other.OutlineColor;
        PositionTop = other.PositionTop;
        PositionRight = other.PositionRight;
        PositionBottom = other.PositionBottom;
        PositionLeft = other.PositionLeft;
        ZIndex = other.ZIndex;
        ScrollbarWidth = other.ScrollbarWidth;
        ScrollbarRadius = other.ScrollbarRadius;
        ScrollbarThumbColor = other.ScrollbarThumbColor;
        ScrollbarTrackColor = other.ScrollbarTrackColor;
        Opacity = other.Opacity;
        BoxShadows = other.BoxShadows;
        TextShadows = other.TextShadows;
        Filter = other.Filter;
        BackdropFilter = other.BackdropFilter;
        BorderRadius = other.BorderRadius;
        FontSize = other.FontSize;
        LineHeight = other.LineHeight;
        Transitions = other.Transitions;
        Animations = other.Animations;
        Transform = other.Transform;
        TransformOrigin = other.TransformOrigin;
        BackgroundColor = other.BackgroundColor;
        BackgroundImage = other.BackgroundImage;
        BackgroundSize = other.BackgroundSize;
        BackgroundPosition = other.BackgroundPosition;
        BackgroundRepeat = other.BackgroundRepeat;
        ObjectFit = other.ObjectFit;
        ObjectPosition = other.ObjectPosition;
        AspectRatioAuto = other.AspectRatioAuto;
        Color = other.Color;
        FontFamily = other.FontFamily;
        FontWeight = other.FontWeight;
        LetterSpacing = other.LetterSpacing;
        TextTransform = other.TextTransform;
        TextDecoration = other.TextDecoration;
        WhiteSpace = other.WhiteSpace;
        TextOverflow = other.TextOverflow;
        _writtenMask = other._writtenMask;
    }

    /// <summary>
    /// The subset of properties that participate in Yoga layout. Comparing only
    /// these lets the renderer decide whether a style change requires a full
    /// layout pass (re-measure + reflow) or just a repaint: color, opacity,
    /// transform, shadows, borders colors, radius, outline, z-index, scrollbar
    /// styling and overflow are all paint-only.
    /// </summary>
    private static readonly string[] LayoutAffectingProperties =
    [
        "display", "flex-direction", "flex-wrap", "align-items", "align-content", "align-self",
        "justify-content", "justify-items", "justify-self", "position", "direction", "box-sizing",
        "width", "height", "min-width", "max-width", "min-height", "max-height",
        "flex", "flex-grow", "flex-shrink", "flex-basis", "aspect-ratio",
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "border", "border-width", "border-top", "border-right", "border-bottom", "border-left",
        "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
        "top", "right", "bottom", "left",
        "gap", "row-gap", "column-gap",
        "font-size", "line-height"
    ];

    // Resolved once: the per-comparison dictionary lookup and the boxing
    // GetValue/ValuesEqual round-trip were both visible in the profiler (this
    // comparison runs for every panel on every cascade pass).
    private static CssProperty[]? _layoutPropsCache;

    private static CssProperty[] LayoutPropsCache
    {
        get
        {
            if (_layoutPropsCache is null)
            {
                var list = new List<CssProperty>();
                foreach (var name in LayoutAffectingProperties)
                    if (CssProperties.TryGet(name, out var property)) list.Add(property);
                _layoutPropsCache = [.. list];
            }

            return _layoutPropsCache;
        }
    }

    private static CssProperty[]? _inheritedPropsCache;

    private static CssProperty[] InheritedPropsCache
    {
        get
        {
            if (_inheritedPropsCache is null)
            {
                var list = new List<CssProperty>();
                foreach (var property in CssProperties.All)
                    if (property.Inherited) list.Add(property);
                _inheritedPropsCache = [.. list];
            }

            return _inheritedPropsCache;
        }
    }

    /// <summary>
    /// True when every property the cascade actually wrote on either style
    /// matches. The two styles are cascade outputs (defaults plus the applied
    /// rules), so a property absent from both masks holds its default on both
    /// sides and cannot differ — only the union of the written sets is worth
    /// comparing. For a panel whose rules are stable (the common repaint case)
    /// this is a handful of properties instead of the full registry, and a
    /// fully default style compares in O(1).
    /// </summary>
    public bool StylesEqual(ComputedStyle other)
    {
        var mask = _writtenMask | other._writtenMask;
        if (mask == 0) return true;
        var lo = (ulong)mask;
        var hi = (ulong)(mask >> 64);
        while (lo != 0)
        {
            var bit = System.Numerics.BitOperations.TrailingZeroCount(lo);
            if (!CssProperties.ByIndex[bit].StylesEqual(this, other)) return false;
            lo &= lo - 1;
        }
        while (hi != 0)
        {
            var bit = System.Numerics.BitOperations.TrailingZeroCount(hi);
            if (!CssProperties.ByIndex[64 + bit].StylesEqual(this, other)) return false;
            hi &= hi - 1;
        }
        return true;
    }

    /// <summary>True when every layout-affecting property matches <paramref name="other"/>.</summary>
    public bool LayoutPropsEqual(ComputedStyle other)
    {
        foreach (var property in LayoutPropsCache)
            if (!property.StylesEqual(this, other)) return false;
        return true;
    }

    /// <summary>
    /// True when every inherited property matches <paramref name="other"/>. The
    /// cascade bakes inherited values (color, opacity, text metrics, shadows)
    /// into each child's computed style, so when an animation or transition
    /// moves one of them on an ancestor, the descendants must be refreshed.
    /// </summary>
    public bool InheritedPropsEqual(ComputedStyle other)
    {
        foreach (var property in InheritedPropsCache)
            if (!property.StylesEqual(this, other)) return false;
        return true;
    }
}
