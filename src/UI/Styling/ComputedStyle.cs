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

    /// <summary>The CSS <c>transition-property</c> list (<c>none</c>, <c>all</c> or comma-separated names).</summary>
    public string TransitionProperty { get; set; } = "none";
    /// <summary>Transition duration in seconds (0 disables the transition).</summary>
    public float TransitionDuration { get; set; }
    /// <summary>Transition timing function (ease, linear, cubic-bezier(...), ...).</summary>
    public string TransitionTimingFunction { get; set; } = "ease";
    /// <summary>Transition delay in seconds; the transition starts after this many seconds (may be negative).</summary>
    public float TransitionDelay { get; set; }

    /// <summary>The CSS <c>animation-name</c> (<c>none</c> or a registered keyframe name).</summary>
    public string AnimationName { get; set; } = "none";
    /// <summary>Animation duration in seconds (0 means the keyframes apply discretely).</summary>
    public float AnimationDuration { get; set; }
    /// <summary>Animation timing function (ease, linear, cubic-bezier(...), steps(...), ...).</summary>
    public string AnimationTimingFunction { get; set; } = "ease";
    /// <summary>Animation iteration count (1 by default; <see cref="float.PositiveInfinity"/> for <c>infinite</c>).</summary>
    public float AnimationIterationCount { get; set; } = 1;
    /// <summary>Animation direction (<c>normal</c>, <c>reverse</c>, <c>alternate</c>, <c>alternate-reverse</c>).</summary>
    public string AnimationDirection { get; set; } = "normal";
    /// <summary>Animation delay in seconds before the first iteration starts (may be negative).</summary>
    public float AnimationDelay { get; set; }
    /// <summary>Animation fill mode (<c>none</c>, <c>forwards</c>, <c>backwards</c>, <c>both</c>).</summary>
    public string AnimationFillMode { get; set; } = "none";
    /// <summary>Animation play state (<c>running</c> or <c>paused</c>); toggling does not restart the animation.</summary>
    public string AnimationPlayState { get; set; } = "running";

    /// <summary>Horizontal translate of the <c>transform</c>, in px (paint-only, never affects layout).</summary>
    public float TranslateX { get; set; }
    /// <summary>Vertical translate of the <c>transform</c>, in px (paint-only, never affects layout).</summary>
    public float TranslateY { get; set; }
    /// <summary>Horizontal scale of the <c>transform</c> (paint-only, never affects layout).</summary>
    public float ScaleX { get; set; } = 1;
    /// <summary>Vertical scale of the <c>transform</c> (paint-only, never affects layout).</summary>
    public float ScaleY { get; set; } = 1;
    /// <summary>Rotation of the <c>transform</c>, in degrees around the box center (paint-only, never affects layout).</summary>
    public float Rotate { get; set; }

    /// <summary>True when the panel carries a non-identity transform.</summary>
    public bool HasTransform => TranslateX != 0 || TranslateY != 0 || ScaleX != 1 || ScaleY != 1 || Rotate != 0;

    public UiColor BackgroundColor { get; set; } = UiColor.Transparent;
    public UiColor Color { get; set; } = UiColor.White;

    public ComputedStyle Clone() => (ComputedStyle)MemberwiseClone();
}
