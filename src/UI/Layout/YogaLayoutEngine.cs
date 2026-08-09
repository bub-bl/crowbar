using Facebook.Yoga;
using SkiaSharp;

namespace Crowbar.UI;

/// <summary>
/// Yoga-compatible flex layout adapter backed by Yoga.Net (the maintained C#
/// port of Meta's Yoga, namespace Facebook.Yoga). Every layout-relevant CSS
/// property maps 1:1 onto a Yoga style property, so Yoga resolves percentages,
/// auto margins, wrapping, absolute positioning, aspect ratio and RTL natively.
/// The tree is rebuilt on every pass and kept independent of the renderer.
/// </summary>
public sealed class YogaLayoutEngine
{
    public int LayoutPasses { get; private set; }

    public void Layout(Panel root, float width, float height, StyleSheet? sheet = null)
    {
        LayoutPasses++;
        ApplyStyles(root, sheet, null);
        var style = root.ComputedStyle;
        var yogaRoot = BuildYogaTree(root);
        // The root always gets an explicit size: the CSS size when set (points
        // or percent, resolved against the viewport) or the full viewport.
        yogaRoot.Style.SetDimension(Dimension.Width, style.Width.IsDefined ? ToSize(style.Width) : StyleSizeLength.Points(width));
        yogaRoot.Style.SetDimension(Dimension.Height, style.Height.IsDefined ? ToSize(style.Height) : StyleSizeLength.Points(height));
        LayoutAlgorithm.CalculateLayout(yogaRoot, width, height, Direction.LTR);
        ReadLayout(root, yogaRoot, 0, 0);
        root.ClearDirty();
    }

    /// <summary>
    /// Re-runs the cascade over the whole tree (without Yoga) and returns true
    /// when any layout-affecting property changed anywhere — in which case the
    /// caller must run a full layout pass. Used for style-only invalidations
    /// (classes, inline styles, pseudo-state): most of the time only paint
    /// changes, and the layout is skipped entirely.
    /// </summary>
    public static bool ApplyStylesTracked(Panel root, StyleSheet? sheet)
    {
        var layoutChanged = false;
        ApplyStylesCore(root, sheet, null, ref layoutChanged);
        return layoutChanged;
    }

    /// <summary>
    /// Re-applies inherited values (color, opacity, text metrics, text-shadow)
    /// from each parent's current composed style without re-running the CSS
    /// cascade. Used when a keyframe animation or transition changed an
    /// inherited property on an ancestor: the children's baked values from the
    /// last cascade are stale. Returns true when a layout-affecting property
    /// changed somewhere (e.g. an animated font-size), in which case the caller
    /// must run a full layout pass. The walk is allocation-free.
    /// </summary>
    public static bool ApplyInheritanceOnly(Panel root)
    {
        var layoutChanged = false;
        foreach (var child in root.Children) ApplyInheritance(child, root.ComputedStyle, ref layoutChanged);
        return layoutChanged;
    }

    private static void ApplyInheritance(Panel panel, ComputedStyle inherited, ref bool layoutChanged)
    {
        var previous = panel.ComputedStyle;
        panel.RestoreRestingStyle();
        ApplyInherited(panel.ComputedStyle, inherited);
        if (!previous.LayoutPropsEqual(panel.ComputedStyle)) layoutChanged = true;
        foreach (var child in panel.Children) ApplyInheritance(child, panel.ComputedStyle, ref layoutChanged);
    }

    private static void ApplyStyles(Panel panel, StyleSheet? sheet, ComputedStyle? inherited)
    {
        var unused = false;
        ApplyStylesCore(panel, sheet, inherited, ref unused);
    }

    private static void ApplyStylesCore(Panel panel, StyleSheet? sheet, ComputedStyle? inherited, ref bool layoutChanged)
    {
        ComputedStyle computed;
        if (sheet is not null)
        {
            computed = sheet.Compute(panel);
        }
        else
        {
            // Without a global sheet the panel's inline styles still apply.
            computed = new ComputedStyle();
            StyleSheet.Apply(computed, panel.InlineStyle);
        }

        var previous = panel.ComputedStyle;
        panel.ApplyComputedStyle(computed);
        ApplyInherited(panel.ComputedStyle, inherited);
        if (!previous.LayoutPropsEqual(panel.ComputedStyle)) layoutChanged = true;
        foreach (var child in panel.Children) ApplyStylesCore(child, sheet, panel.ComputedStyle, ref layoutChanged);
    }

    /// <summary>
    /// Applies the inherited-value block: opacity multiplies, and the text
    /// metrics/color/shadow sentinels flow down when the child did not declare
    /// its own. Mirrored by <see cref="ApplyInheritance"/> so the animation
    /// refresh pass stays in sync with the cascade.
    /// </summary>
    private static void ApplyInherited(ComputedStyle style, ComputedStyle? inherited)
    {
        if (inherited is null) return;
        if (style.Color == UiColor.White) style.Color = inherited.Color;
        style.Opacity *= inherited.Opacity;
        // Text is rendered by the leaf label/input inside controls such as
        // Button. Carry the inherited text metrics down so the leaf uses
        // the same alignment and line box as its parent.
        if (style.TextAlign == "left") style.TextAlign = inherited.TextAlign;
        if (style.VerticalAlign == "top") style.VerticalAlign = inherited.VerticalAlign;
        if (Math.Abs(style.FontSize - 16) < 0.0001f) style.FontSize = inherited.FontSize;
        if (style.LineHeight == 0) style.LineHeight = inherited.LineHeight;
        // text-shadow inherits like color: carry the parent's list down when
        // the child did not declare one of its own.
        if (style.TextShadows.Length == 0) style.TextShadows = inherited.TextShadows;
    }

    private static Node BuildYogaTree(Panel panel)
    {
        var style = panel.ComputedStyle;
        var node = new Node(Config.Default)
        {
            Style =
            {
                Direction = ParseDirection(style.Direction),
                FlexDirection = ParseFlexDirection(style.FlexDirection),
                JustifyContent = ParseJustify(style.JustifyContent),
                JustifyItems = ParseJustify(style.JustifyItems),
                JustifySelf = ParseJustify(style.JustifySelf),
                AlignContent = ParseAlign(style.AlignContent),
                AlignItems = ParseAlign(style.AlignItems),
                AlignSelf = ParseAlign(style.AlignSelf),
                PositionType = ParsePositionType(style.PositionType),
                FlexWrap = ParseWrap(style.FlexWrap),
                Display = ParseDisplay(style.Display, panel.IsVisible),
                Overflow = ParseOverflow(style.Overflow),
                BoxSizing = style.BoxSizing.Equals("content-box", StringComparison.OrdinalIgnoreCase) ? BoxSizing.ContentBox : BoxSizing.BorderBox,
            }
        };
        node.SetContext(panel);

        ApplyFlex(node, style);
        ApplyDimensions(node, style);
        ApplyBoxEdges(node, style);
        ApplyPositionOffsets(node, style);
        ApplyGaps(node, style);
        if (style.AspectRatio > 0) node.Style.AspectRatio = new FloatOptional(style.AspectRatio);
        ApplyTextMeasure(node, panel, style);

        for (var i = 0; i < panel.Children.Count; i++)
        {
            var child = BuildYogaTree(panel.Children[i]);
            node.InsertChild(child, (nuint)i);
            child.SetOwner(node);
        }
        return node;
    }

    private static void ApplyFlex(Node node, ComputedStyle style)
    {
        node.Style.FlexGrow = new FloatOptional(style.FlexGrow);
        if (style.FlexShrink != 0) node.Style.FlexShrink = new FloatOptional(style.FlexShrink);
        if (style.FlexBasis.IsDefined) node.Style.FlexBasis = ToSize(style.FlexBasis);
    }

    private static void ApplyDimensions(Node node, ComputedStyle style)
    {
        node.Style.SetDimension(Dimension.Width, ToSize(style.Width));
        node.Style.SetDimension(Dimension.Height, ToSize(style.Height));
        node.Style.SetMinDimension(Dimension.Width, ToSize(style.MinWidth));
        node.Style.SetMaxDimension(Dimension.Width, ToSize(style.MaxWidth));
        node.Style.SetMinDimension(Dimension.Height, ToSize(style.MinHeight));
        node.Style.SetMaxDimension(Dimension.Height, ToSize(style.MaxHeight));
    }

    private static void ApplyBoxEdges(Node node, ComputedStyle style)
    {
        SetMargin(node, Edge.Top, style.MarginTop);
        SetMargin(node, Edge.Right, style.MarginRight);
        SetMargin(node, Edge.Bottom, style.MarginBottom);
        SetMargin(node, Edge.Left, style.MarginLeft);
        SetPadding(node, Edge.Top, style.PaddingTop);
        SetPadding(node, Edge.Right, style.PaddingRight);
        SetPadding(node, Edge.Bottom, style.PaddingBottom);
        SetPadding(node, Edge.Left, style.PaddingLeft);
        SetBorder(node, Edge.Top, style.BorderTop);
        SetBorder(node, Edge.Right, style.BorderRight);
        SetBorder(node, Edge.Bottom, style.BorderBottom);
        SetBorder(node, Edge.Left, style.BorderLeft);
    }

    private static void ApplyPositionOffsets(Node node, ComputedStyle style)
    {
        if (style.PositionTop.IsDefined) node.Style.SetPosition(Edge.Top, ToLength(style.PositionTop));
        if (style.PositionRight.IsDefined) node.Style.SetPosition(Edge.Right, ToLength(style.PositionRight));
        if (style.PositionBottom.IsDefined) node.Style.SetPosition(Edge.Bottom, ToLength(style.PositionBottom));
        if (style.PositionLeft.IsDefined) node.Style.SetPosition(Edge.Left, ToLength(style.PositionLeft));
    }

    private static void ApplyGaps(Node node, ComputedStyle style)
    {
        // Yoga.Net supports gap natively (Facebook.Yoga had to fake it through
        // child margins), so the row/column gaps map straight to gutters.
        if (style.ColumnGap.IsDefined) node.Style.SetGap(Gutter.Column, ToLength(style.ColumnGap));
        if (style.RowGap.IsDefined) node.Style.SetGap(Gutter.Row, ToLength(style.RowGap));
    }

    private static void ApplyTextMeasure(Node node, Panel panel, ComputedStyle style)
    {
        if ((panel.TagName.Equals("text", StringComparison.OrdinalIgnoreCase) || panel is TextInput) && !string.IsNullOrEmpty(panel is TextInput input ? input.Value : panel.Text))
        {
            var text = panel is TextInput inputValue ? inputValue.Value : panel.Text;
            var fontSize = style.FontSize;
            var lineHeight = style.LineHeight > 0 ? style.LineHeight : style.FontSize * 1.25f;
            // Yoga treats the measure result as the content box and adds the
            // node's padding/border around it, so the callback must only size
            // the text itself. The text is measured with the same Skia font as
            // the renderer so the layout box matches the drawn glyphs.
            var textWidth = 0f;
            using (var font = new SKFont { Size = fontSize })
            {
                foreach (var line in text.Split('\n'))
                    textWidth = Math.Max(textWidth, font.MeasureText(line));
            }
            node.SetMeasureFunc((_, availableWidth, _, _, _) => new YGSize
            {
                Width = Math.Min(availableWidth > 0 ? availableWidth : float.MaxValue, textWidth),
                Height = lineHeight
            });
        }
    }

    private static void ReadLayout(Panel panel, Node node, float parentX, float parentY)
    {
        var layout = node.Layout;
        panel.Layout = new UiRect(
            parentX + layout.Position(PhysicalEdge.Left),
            parentY + layout.Position(PhysicalEdge.Top),
            layout.Dimension(Dimension.Width),
            layout.Dimension(Dimension.Height));
        // Yoga resolves every length (including percentages) during layout;
        // the renderer consumes these resolved values instead of re-deriving
        // them from the computed style.
        // UiThickness stores (Top, Right, Bottom, Left) — keep the edges in
        // that order (a previous swap pushed text 12px down on asymmetric
        // paddings like `padding: 0 12px`).
        panel.LayoutPadding = new UiThickness(
            layout.Padding(PhysicalEdge.Top),
            layout.Padding(PhysicalEdge.Right),
            layout.Padding(PhysicalEdge.Bottom),
            layout.Padding(PhysicalEdge.Left));
        panel.LayoutBorder = new UiThickness(
            layout.Border(PhysicalEdge.Top),
            layout.Border(PhysicalEdge.Right),
            layout.Border(PhysicalEdge.Bottom),
            layout.Border(PhysicalEdge.Left));
        panel.LayoutMargin = new UiThickness(
            layout.Margin(PhysicalEdge.Top),
            layout.Margin(PhysicalEdge.Right),
            layout.Margin(PhysicalEdge.Bottom),
            layout.Margin(PhysicalEdge.Left));
        for (var i = 0; i < panel.Children.Count && i < (int)node.GetChildCount(); i++)
            ReadLayout(panel.Children[i], node.GetChild((nuint)i)!, panel.Layout.X, panel.Layout.Y);
        ComputeScrollRange(panel);
    }

    /// <summary>
    /// Computes the scrollable range of a panel from the laid-out bounds of its
    /// children (including their margins, as CSS does). Content starts at the
    /// padding-box origin; anything extending past the client box is scrollable.
    /// </summary>
    private static void ComputeScrollRange(Panel panel)
    {
        panel.MaxScrollX = 0;
        panel.MaxScrollY = 0;
        if (panel.Children.Count == 0) return;
        var contentRight = 0f;
        var contentBottom = 0f;
        foreach (var child in panel.Children)
        {
            contentRight = Math.Max(contentRight, child.Layout.Right + child.LayoutMargin.Right - panel.Layout.X);
            contentBottom = Math.Max(contentBottom, child.Layout.Bottom + child.LayoutMargin.Bottom - panel.Layout.Y);
        }
        var clientLeft = panel.LayoutPadding.Left + panel.LayoutBorder.Left;
        var clientTop = panel.LayoutPadding.Top + panel.LayoutBorder.Top;
        var contentWidth = Math.Max(0, contentRight - clientLeft);
        var contentHeight = Math.Max(0, contentBottom - clientTop);
        panel.MaxScrollX = Math.Max(0, contentWidth - panel.ClientWidth);
        panel.MaxScrollY = Math.Max(0, contentHeight - panel.ClientHeight);
        // Keep the current offset valid when the content shrank. ScrollTo is a
        // no-op (no invalidation) when the offset is already in range.
        panel.ScrollTo(panel.ScrollX, panel.ScrollY);
    }

    private static void SetMargin(Node node, Edge edge, CssLength value)
    {
        if (value.IsDefined) node.Style.SetMargin(edge, ToLength(value));
    }

    private static void SetPadding(Node node, Edge edge, CssLength value)
    {
        if (value.IsDefined) node.Style.SetPadding(edge, ToLength(value));
    }

    private static void SetBorder(Node node, Edge edge, CssLength value)
    {
        if (value.IsDefined) node.Style.SetBorder(edge, ToLength(value));
    }

    private static StyleSizeLength ToSize(CssLength value) => value.Unit switch
    {
        CssLengthUnit.Points => StyleSizeLength.Points(value.Value),
        CssLengthUnit.Percent => StyleSizeLength.Percent(value.Value),
        CssLengthUnit.Auto => StyleSizeLength.OfAuto(),
        CssLengthUnit.MaxContent => StyleSizeLength.OfMaxContent(),
        CssLengthUnit.FitContent => StyleSizeLength.OfFitContent(),
        _ => StyleSizeLength.Undefined()
    };

    private static StyleLength ToLength(CssLength value) => value.Unit switch
    {
        CssLengthUnit.Points => StyleLength.Points(value.Value),
        CssLengthUnit.Percent => StyleLength.Percent(value.Value),
        CssLengthUnit.Auto => StyleLength.OfAuto(),
        _ => StyleLength.Undefined()
    };

    private static FlexDirection ParseFlexDirection(string value) => value.ToLowerInvariant() switch
    {
        "row" => FlexDirection.Row,
        "row-reverse" => FlexDirection.RowReverse,
        "column-reverse" => FlexDirection.ColumnReverse,
        _ => FlexDirection.Column
    };

    private static Wrap ParseWrap(string value) => value.ToLowerInvariant() switch
    {
        "wrap" => Wrap.Wrap,
        "wrap-reverse" => Wrap.WrapReverse,
        _ => Wrap.NoWrap
    };

    private static PositionType ParsePositionType(string value) => value.ToLowerInvariant() switch
    {
        "absolute" => PositionType.Absolute,
        "static" => PositionType.Static,
        _ => PositionType.Relative
    };

    private static Direction ParseDirection(string value) => value.ToLowerInvariant() switch
    {
        "ltr" => Direction.LTR,
        "rtl" => Direction.RTL,
        _ => Direction.Inherit
    };

    private static Display ParseDisplay(string value, bool visible) => !visible ? Display.None : value.ToLowerInvariant() switch
    {
        "none" => Display.None,
        "contents" => Display.Contents,
        _ => Display.Flex
    };

    private static Overflow ParseOverflow(string value) => value.ToLowerInvariant() switch
    {
        "hidden" => Overflow.Hidden,
        "scroll" => Overflow.Scroll,
        _ => Overflow.Visible
    };

    private static Align ParseAlign(string value) => value.ToLowerInvariant() switch
    {
        "auto" => Align.Auto,
        "flex-start" => Align.FlexStart,
        "center" => Align.Center,
        "flex-end" => Align.FlexEnd,
        "stretch" => Align.Stretch,
        "baseline" => Align.Baseline,
        "space-between" => Align.SpaceBetween,
        "space-around" => Align.SpaceAround,
        "space-evenly" => Align.SpaceEvenly,
        "start" => Align.Start,
        "end" => Align.End,
        _ => Align.Stretch
    };

    private static Justify ParseJustify(string value) => value.ToLowerInvariant() switch
    {
        "auto" => Justify.Auto,
        "flex-start" => Justify.FlexStart,
        "center" => Justify.Center,
        "flex-end" => Justify.FlexEnd,
        "stretch" => Justify.Stretch,
        "space-between" => Justify.SpaceBetween,
        "space-around" => Justify.SpaceAround,
        "space-evenly" => Justify.SpaceEvenly,
        "start" => Justify.Start,
        "end" => Justify.End,
        _ => Justify.FlexStart
    };
}
