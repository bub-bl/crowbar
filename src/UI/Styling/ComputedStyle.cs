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
    public UiColor Color { get; set; } = UiColor.White;

    public ComputedStyle Clone() => (ComputedStyle)MemberwiseClone();

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

    /// <summary>True when every layout-affecting property matches <paramref name="other"/>.</summary>
    public bool LayoutPropsEqual(ComputedStyle other)
    {
        foreach (var name in LayoutAffectingProperties)
        {
            if (!CssProperties.TryGet(name, out var property)) continue;
            if (!property.ValuesEqual(property.GetValue(this), property.GetValue(other))) return false;
        }
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
        foreach (var property in CssProperties.All)
        {
            if (!property.Inherited) continue;
            if (!property.ValuesEqual(property.GetValue(this), property.GetValue(other))) return false;
        }
        return true;
    }
}
