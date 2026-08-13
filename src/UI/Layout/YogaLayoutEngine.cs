using System.Runtime.CompilerServices;
using Facebook.Yoga;

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
    /// <summary>Per-panel Yoga state kept across passes so layout is incremental.</summary>
    private sealed class YogaNodeState
    {
        public Node? Node;
        /// <summary>Identity of the measure inputs the func was built for (text, source, name).</summary>
        public string? MeasureSig;
        /// <summary>Hash of the measure-relevant numeric style properties.</summary>
        public long MeasureKey;
        public bool HasMeasureFunc;
        /// <summary>The panel's cascade version the node's style was last applied from.</summary>
        public int StyleVersion;
        /// <summary>The panel's visibility the node's Display was last applied from.</summary>
        public bool StyleVisible;
    }

    /// <summary>
    /// Panel → Yoga node state. Keyed weakly: panels replaced by a Razor rebuild
    /// are dropped as soon as the tree stops referencing them, so the table does
    /// not leak across the editor's lifetime.
    /// </summary>
    private readonly ConditionalWeakTable<Panel, YogaNodeState> _states = new();

    public int LayoutPasses { get; private set; }

    public void Layout(Panel root, float width, float height, StyleSheet? sheet = null, UiImageCache? cache = null, SvgIconCache? iconCache = null)
    {
        LayoutPasses++;
        // The prepare pass already re-cascaded every panel a change can affect
        // (ApplyStylesTracked's scoped walk), so this walk skips the clean rest
        // of the tree. Direct callers (tests) build fresh trees whose panels
        // start style-dirty, so they are always fully cascaded.
        var unused = false;
        ApplyStylesCore(root, sheet, null, ref unused, skipClean: true);
        var style = root.ComputedStyle;
        var yogaRoot = GetOrCreateNode(root);
        // The root always gets an explicit size: the CSS size when set (points
        // or percent, resolved against the viewport) or the full viewport.
        yogaRoot.Style.SetDimension(Dimension.Width, style.Width.IsDefined ? ToSize(style.Width) : StyleSizeLength.Points(width));
        yogaRoot.Style.SetDimension(Dimension.Height, style.Height.IsDefined ? ToSize(style.Height) : StyleSizeLength.Points(height));
        SyncNode(root, yogaRoot, cache ?? UiImageCache.Shared, iconCache);
        LayoutAlgorithm.CalculateLayout(yogaRoot, width, height, Direction.LTR);
        ReadLayout(root, yogaRoot, 0, 0);
        root.ClearDirty();
    }

    /// <summary>
    /// Re-runs the cascade (without Yoga) and returns true when any
    /// layout-affecting property changed anywhere — in which case the caller
    /// must run a full layout pass. Used for style-only invalidations
    /// (classes, inline styles, pseudo-state): most of the time only paint
    /// changes, and the layout is skipped entirely.
    /// </summary>
    /// <remarks>
    /// The re-cascade is scoped to the subtrees that can actually be affected:
    /// a mutated panel's state can change the match of descendant/child
    /// selectors (its descendants) and of adjacent/general-sibling selectors
    /// (its siblings and their descendants), so the parent's subtree is the
    /// affected set when sibling combinators exist. Without sibling
    /// combinators (and without <c>:focus-within</c>, the one pseudo-class that
    /// matches ancestors), a panel's change can only affect its own subtree —
    /// so the re-cascade skips the siblings entirely. Re-cascading the whole
    /// tree (or the whole parent subtree) for a single hover change was the
    /// dominant per-frame cost in the profiler.
    /// </remarks>
    public static bool ApplyStylesTracked(Panel root, StyleSheet? sheet)
    {
        var layoutChanged = false;
        if (root is ScreenPanel screen && screen.StyleDirtyRoots.Count > 0)
        {
            var mayAffectLayout = screen.StyleDirtyRoots.Any(panel => panel.StyleMayAffectLayout);
            var scopedToDirtyRoot = sheet is null || (!sheet.HasSiblingRules && !sheet.HasFocusWithinRules);
            foreach (var dirty in screen.StyleDirtyRoots)
            {
                if (scopedToDirtyRoot)
                {
                    ApplyStylesCore(dirty, sheet, dirty.Parent?.ComputedStyle, ref layoutChanged);
                }
                else
                {
                    var subtree = dirty.Parent ?? root;
                    ApplyStylesCore(subtree, sheet, subtree.Parent?.ComputedStyle, ref layoutChanged);
                }
            }

            // Pseudo-state changes (hover/pressed/focus) normally affect only
            // paint. Avoid escalating their cascade to Yoga when no mutation
            // that can change geometry was recorded.
            return mayAffectLayout && layoutChanged;
        }

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
        foreach (var child in root.ChildrenInternal) ApplyInheritance(child, root.ComputedStyle, ref layoutChanged);
        return layoutChanged;
    }

    /// <summary>
    /// Re-applies inherited values to the subtree of <paramref name="root"/>
    /// (the panel whose animation moved an inherited property). The refresh is
    /// scoped to the affected subtree instead of walking the whole tree, so a
    /// continuously animated leaf (an infinite color/text-shadow pulse) does
    /// not force an O(tree) inheritance pass every frame.
    /// </summary>
    public static bool ApplyInheritanceSubtree(Panel root)
    {
        var layoutChanged = false;
        foreach (var child in root.ChildrenInternal) ApplyInheritance(child, root.ComputedStyle, ref layoutChanged);
        return layoutChanged;
    }

    private static void ApplyInheritance(Panel panel, ComputedStyle inherited, ref bool layoutChanged)
    {
        var previous = panel.ComputedStyle;
        panel.RestoreRestingStyle();
        ApplyInherited(panel.ComputedStyle, inherited);
        if (!previous.LayoutPropsEqual(panel.ComputedStyle)) layoutChanged = true;
        foreach (var child in panel.ChildrenInternal) ApplyInheritance(child, panel.ComputedStyle, ref layoutChanged);
    }

    private static void ApplyStylesCore(Panel panel, StyleSheet? sheet, ComputedStyle? inherited, ref bool layoutChanged, bool skipClean = false)
    {
        // Layout's full walk may skip panels whose own cascade inputs are
        // unchanged: every panel a change can affect was already re-cascaded by
        // ApplyStylesTracked's scoped walk in the prepare pass, so re-cascading
        // the clean rest of the tree would be redundant. The scoped walk itself
        // never skips (a dirty root's whole subtree must refresh inherited
        // values), and un-cascaded panels always cascade.
        if (skipClean && !panel.StyleDirty && panel.HasComputedStyle)
            return;

        // The cascade fills the panel's reusable compute buffer (reset to
        // defaults in place), so a style-stable panel allocates no
        // ComputedStyle per pass. Without a sheet the panel's inline styles
        // still apply.
        var computed = panel.ComputeStyle(sheet);

        var previous = panel.ComputedStyle;
        panel.ApplyComputedStyle(computed);
        ApplyInherited(panel.ComputedStyle, inherited);
        // Generated ::before/::after content is recomputed with the panel's
        // fresh computed style as the inheritance base, so a class change or
        // re-cascade refreshes the pseudo elements too.
        if (sheet is not null)
        {
            // Pseudo rules are rare; skip the (cheap) lookup entirely when the
            // sheet carries no rule for the pseudo element.
            panel.PseudoBefore = sheet.HasPseudoRules("before")
                && sheet.TryComputePseudo(panel, "before", panel.ComputedStyle, out var beforeText, out var beforeStyle)
                && beforeText is not null
                ? new Panel.PseudoContent(beforeText, beforeStyle)
                : null;
            panel.PseudoAfter = sheet.HasPseudoRules("after")
                && sheet.TryComputePseudo(panel, "after", panel.ComputedStyle, out var afterText, out var afterStyle)
                && afterText is not null
                ? new Panel.PseudoContent(afterText, afterStyle)
                : null;
        }
        else
        {
            panel.PseudoBefore = null;
            panel.PseudoAfter = null;
        }
        if (!previous.LayoutPropsEqual(panel.ComputedStyle)) layoutChanged = true;
        // display:none subtrees contribute neither layout nor paint. Do not
        // cascade every descendant of an inactive dock tab on every layout
        // pass; when the tab becomes visible, its parent is style-dirty and
        // this branch is traversed again with the fresh inherited style.
        if (panel.ComputedStyle.Display.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var child in panel.ChildrenInternal) ApplyStylesCore(child, sheet, panel.ComputedStyle, ref layoutChanged, skipClean);
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
        // Typography inherits like color: carry the parent's family/weight/
        // tracking/case/decoration/whitespace down when the child did not
        // declare its own.
        if (style.FontFamily == "sans-serif") style.FontFamily = inherited.FontFamily;
        if (style.FontWeight == 400) style.FontWeight = inherited.FontWeight;
        if (style.LetterSpacing == 0) style.LetterSpacing = inherited.LetterSpacing;
        if (style.TextTransform == "none") style.TextTransform = inherited.TextTransform;
        if (style.WhiteSpace == "normal") style.WhiteSpace = inherited.WhiteSpace;
        if (style.TextDecoration == "none") style.TextDecoration = inherited.TextDecoration;
        // text-shadow inherits like color: carry the parent's list down when
        // the child did not declare one of its own.
        if (style.TextShadows.Length == 0) style.TextShadows = inherited.TextShadows;
    }

    private YogaNodeState GetOrCreateState(Panel panel) => _states.GetValue(panel, _ => new YogaNodeState());

    private Node GetOrCreateNode(Panel panel)
    {
        var state = GetOrCreateState(panel);
        if (state.Node is null)
        {
            state.Node = new Node(Config.Default);
            state.Node.SetContext(panel);
        }
        return state.Node;
    }

    /// <summary>
    /// Reconciles the panel's Yoga node with the panel's current state, reusing
    /// the node across passes. Style setters are idempotent (Yoga marks a node
    /// dirty only when a value actually changed), so a panel whose layout is
    /// unchanged costs nothing and keeps its cached layout — Yoga skips clean
    /// subtrees entirely during CalculateLayout. Structural edits touch only
    /// the nodes that changed: the child list is merged by identity, leaving
    /// already-correct children untouched, so a stable tree never disturbs
    /// Yoga's caches.
    /// </summary>
    private void SyncNode(Panel panel, Node node, UiImageCache cache, SvgIconCache? iconCache)
    {
        ApplyYogaStyle(node, panel, cache, iconCache);

        // Yoga.Net's style setters store the value but do not mark the node
        // dirty, so a reused node would keep its previous layout. Mark the node
        // explicitly whenever the cascaded style (or the derived visibility)
        // moved since the last pass; unchanged panels stay clean and Yoga
        // reuses their cached layout.
        var state = GetOrCreateState(panel);
        if (state.StyleVersion != panel.CascadeVersion || state.StyleVisible != panel.IsVisible)
        {
            state.StyleVersion = panel.CascadeVersion;
            state.StyleVisible = panel.IsVisible;
            node.MarkDirtyAndPropagate();
        }

        var displayNone = panel.ComputedStyle.Display.Equals("none", StringComparison.OrdinalIgnoreCase);
        var children = displayNone ? (IReadOnlyList<Panel>)Array.Empty<Panel>() : panel.ChildrenInternal;
        var count = (int)node.GetChildCount();

        if (count == children.Count)
        {
            var inSync = true;
            for (var c = 0; c < count; c++)
            {
                if (!ReferenceEquals(node.GetChild((nuint)c).GetContext(), children[c]))
                {
                    inSync = false;
                    break;
                }
            }
            if (inSync)
            {
                for (var c = 0; c < count; c++)
                    SyncNode(children[c], node.GetChild((nuint)c), cache, iconCache);
                return;
            }
        }

        // Structural change: reconcile the child list by identity. Yoga.Net's
        // child edits do not mark the parent dirty, so a reused parent whose
        // cached layout is otherwise valid would keep it and the new children
        // would never be laid out (their dimensions stay undefined). Mark the
        // parent explicitly; the recompute stays incremental because clean
        // grandchildren keep their cached internal layout.
        node.MarkDirtyAndPropagate();
        var visible = new HashSet<Panel>(children);
        for (var c = count - 1; c >= 0; c--)
        {
            var childNode = node.GetChild((nuint)c);
            if (childNode.GetContext() is not Panel childPanel || !visible.Contains(childPanel))
            {
                node.RemoveChild(childNode);
                childNode.SetOwner(null);
            }
        }
        for (var c = 0; c < children.Count; c++)
        {
            var expected = GetOrCreateNode(children[c]);
            var atIndex = c < (int)node.GetChildCount() ? node.GetChild((nuint)c) : null;
            if (ReferenceEquals(atIndex, expected)) continue;

            // Detach from wherever it currently sits (another parent, or a
            // different index of this same node).
            if (expected.GetOwner() is { } oldParent && !ReferenceEquals(oldParent, node))
            {
                oldParent.RemoveChild(expected);
                expected.SetOwner(null);
            }
            else if (ReferenceEquals(expected.GetOwner(), node))
            {
                for (var k = 0; k < (int)node.GetChildCount(); k++)
                {
                    if (ReferenceEquals(node.GetChild((nuint)k), expected))
                    {
                        node.RemoveChild(node.GetChild((nuint)k));
                        expected.SetOwner(null);
                        break;
                    }
                }
            }
            node.InsertChild(expected, (nuint)c);
            expected.SetOwner(node);
        }
        for (var c = 0; c < children.Count; c++)
            SyncNode(children[c], node.GetChild((nuint)c), cache, iconCache);
    }

    /// <summary>
    /// Applies the panel's computed style to its Yoga node in place. Every
    /// setter is called unconditionally (even for default/undefined values) so
    /// a reused node clears a previous explicit value; Yoga's setters compare
    /// the old value and only mark the node dirty on an actual change.
    /// </summary>
    private void ApplyYogaStyle(Node node, Panel panel, UiImageCache cache, SvgIconCache? iconCache)
    {
        var style = panel.ComputedStyle;
        node.Style.Direction = ParseDirection(style.Direction);
        node.Style.FlexDirection = ParseFlexDirection(style.FlexDirection);
        node.Style.JustifyContent = ParseJustify(style.JustifyContent);
        node.Style.JustifyItems = ParseJustify(style.JustifyItems);
        node.Style.JustifySelf = ParseJustify(style.JustifySelf);
        node.Style.AlignContent = ParseAlign(style.AlignContent);
        node.Style.AlignItems = ParseAlign(style.AlignItems);
        node.Style.AlignSelf = ParseAlign(style.AlignSelf);
        node.Style.PositionType = ParsePositionType(style.PositionType);
        node.Style.FlexWrap = ParseWrap(style.FlexWrap);
        node.Style.Display = ParseDisplay(style.Display, panel.IsVisible);
        node.Style.Overflow = ParseOverflow(style.Overflow);
        node.Style.BoxSizing = style.BoxSizing.Equals("content-box", StringComparison.OrdinalIgnoreCase) ? BoxSizing.ContentBox : BoxSizing.BorderBox;

        node.Style.FlexGrow = new FloatOptional(style.FlexGrow);
        node.Style.FlexShrink = new FloatOptional(style.FlexShrink);
        node.Style.FlexBasis = ToSize(style.FlexBasis);
        ApplyDimensions(node, style);
        ApplyBoxEdges(node, style);
        ApplyPositionOffsets(node, style);
        ApplyGaps(node, style);

        // aspect-ratio: the declared ratio drives Yoga unless `auto` is set and
        // the image carries an intrinsic ratio — then the image wins, matching
        // CSS (an image with `aspect-ratio: auto` sizes by its pixels).
        var intrinsicRatio = 0f;
        if (panel is Image image && !string.IsNullOrEmpty(image.Source) &&
            cache.TryGetSize(image.Source, out var imageWidth, out var imageHeight) && imageHeight > 0)
            intrinsicRatio = imageWidth / imageHeight;
        node.Style.AspectRatio = new FloatOptional(
            style.AspectRatioAuto ? (intrinsicRatio > 0 ? intrinsicRatio : 0f) : (style.AspectRatio > 0 ? style.AspectRatio : 0f));
        ApplyTextMeasure(node, panel, style, cache, intrinsicRatio, iconCache);
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
        // An undefined offset stays Undefined (never coerced to 0): with
        // `top:auto; bottom:0`, forcing top to 0 pins the element to the top
        // edge and the bottom is ignored (over-constrained absolute layout
        // prefers the start edge). Yoga.Net's setter stores Undefined and
        // clears any previously set value, so a reused node that lost an
        // explicit offset (e.g. a status bar that stops declaring top) also
        // resets correctly.
        node.Style.SetPosition(Edge.Top, ToLength(style.PositionTop));
        node.Style.SetPosition(Edge.Right, ToLength(style.PositionRight));
        node.Style.SetPosition(Edge.Bottom, ToLength(style.PositionBottom));
        node.Style.SetPosition(Edge.Left, ToLength(style.PositionLeft));
    }

    private static void ApplyGaps(Node node, ComputedStyle style)
    {
        // Yoga.Net supports gap natively (Facebook.Yoga had to fake it through
        // child margins), so the row/column gaps map straight to gutters.
        node.Style.SetGap(Gutter.Column, ToLengthOrZero(style.ColumnGap));
        node.Style.SetGap(Gutter.Row, ToLengthOrZero(style.RowGap));
    }

    private static long MeasureKey(ComputedStyle style) => HashCode.Combine(
        style.FontSize, style.LineHeight, style.FontFamily, style.FontWeight, style.LetterSpacing,
        style.TextTransform, style.WhiteSpace, style.TextOverflow);

    /// <summary>
    /// Installs the intrinsic-measure function on <paramref name="node"/> only
    /// when the measure inputs changed since the last pass. Nodes are reused
    /// across passes, and Yoga.Net does not mark a node dirty when its measure
    /// function is replaced — so a changed signature explicitly calls
    /// <see cref="Node.MarkDirtyAndPropagate"/> to force a re-measure, while
    /// unchanged panels keep their cached measure result (re-setting the same
    /// function would dirty the node and defeat incremental layout). The
    /// closures read the panel's live state, so a reused node always sizes by
    /// the current text and computed style.
    /// </summary>
    private void ApplyTextMeasure(Node node, Panel panel, ComputedStyle style, UiImageCache cache, float intrinsicRatio, SvgIconCache? iconCache = null)
    {
        var state = GetOrCreateState(panel);
        var text = panel is TextInput input ? input.Value : panel.Text;
        if ((panel.TagName.Equals("text", StringComparison.OrdinalIgnoreCase) || panel is TextInput) && text.Length > 0)
        {
            // No pseudo content: displayText is the panel's own string instance,
            // so an unchanged panel compares by reference and never re-measures.
            var before = panel.PseudoBefore?.Text;
            var after = panel.PseudoAfter?.Text;
            var displayText = before is null && after is null
                ? text
                : (before ?? string.Empty) + text + (after ?? string.Empty);
            var key = MeasureKey(style);
            if (!state.HasMeasureFunc || !Equals(state.MeasureSig, displayText) || state.MeasureKey != key)
            {
                state.HasMeasureFunc = true;
                state.MeasureSig = displayText;
                state.MeasureKey = key;
                node.SetMeasureFunc((_, availableWidth, widthMode, _, _) =>
                {
                    // Yoga treats the measure result as the content box and adds
                    // the node's padding/border around it, so the callback must
                    // only size the text itself. The text is measured with the
                    // same font options as the GPU renderer so the layout box
                    // matches the drawn glyphs. ::before/::after content joins
                    // the text as one display flow.
                    var current = panel.ComputedStyle;
                    var lineHeight = current.LineHeight > 0 ? current.LineHeight : current.FontSize * 1.25f;
                    var font = TextLayout.CreateFont(current);
                    var transformed = TextLayout.ApplyTransform(displayText, current.TextTransform);
                    var wrapWidth = widthMode == MeasureMode.Undefined || availableWidth <= 0
                        ? float.MaxValue
                        : availableWidth;
                    var lines = TextLayout.Wrap(transformed, font, wrapWidth, current.WhiteSpace, current.LetterSpacing,
                        current.TextOverflow);
                    var width = 0f;
                    foreach (var line in lines)
                        width = Math.Max(width, TextLayout.Measure(font, line, current.LetterSpacing));
                    return new YGSize
                    {
                        Width = availableWidth > 0 ? Math.Min(availableWidth, width) : width,
                        Height = Math.Max(lineHeight, lines.Count * lineHeight)
                    };
                });
                node.MarkDirtyAndPropagate();
            }
            return;
        }

        if (panel is ToggleInput)
        {
            // A checkbox/radio without explicit dimensions sizes to its
            // intrinsic indicator box (like a native form control).
            if (!state.HasMeasureFunc)
            {
                state.HasMeasureFunc = true;
                state.MeasureSig = "toggle";
                state.MeasureKey = 0;
                node.SetMeasureFunc((_, _, _, _, _) => new YGSize { Width = 16, Height = 16 });
            }
            return;
        }

        if (panel is Image image && !string.IsNullOrEmpty(image.Source) &&
            cache.TryGetSize(image.Source, out var imageWidth, out var imageHeight) && imageWidth > 0 && imageHeight > 0)
        {
            // An <img> without explicit dimensions sizes to its intrinsic
            // pixels; with one axis fixed, the other follows the ratio (the
            // measure callback receives the resolved constraint per axis).
            if (!state.HasMeasureFunc || !Equals(state.MeasureSig, image.Source))
            {
                state.HasMeasureFunc = true;
                state.MeasureSig = image.Source;
                state.MeasureKey = 0;
                var ratio = imageWidth / imageHeight;
                node.SetMeasureFunc((_, width, widthMode, height, heightMode) =>
                {
                    if (heightMode == MeasureMode.Exactly && height > 0)
                        return new YGSize { Width = height * ratio, Height = height };
                    if (widthMode == MeasureMode.Exactly && width > 0)
                        return new YGSize { Width = width, Height = width / ratio };
                    return new YGSize { Width = imageWidth, Height = imageHeight };
                });
                node.MarkDirtyAndPropagate();
            }
            return;
        }

        if (panel is Icon icon && !string.IsNullOrEmpty(icon.Name))
        {
            // An <icon> without explicit dimensions sizes to its intrinsic SVG
            // ratio at a 16x16 default; with one axis fixed, the other follows
            // the ratio (icons are usually square, so both stay 16px).
            if (!state.HasMeasureFunc || !Equals(state.MeasureSig, icon.Name))
            {
                state.HasMeasureFunc = true;
                state.MeasureSig = icon.Name;
                state.MeasureKey = 0;
                var ratio = iconCache is not null && iconCache.TryGetIntrinsicSize(icon.Name, out var iw, out var ih) && ih > 0
                    ? iw / ih
                    : 1f;
                const float defaultSize = 16f;
                node.SetMeasureFunc((_, width, widthMode, height, heightMode) =>
                {
                    if (heightMode == MeasureMode.Exactly && height > 0)
                        return new YGSize { Width = height * ratio, Height = height };
                    if (widthMode == MeasureMode.Exactly && width > 0)
                        return new YGSize { Width = width, Height = width / ratio };
                    return new YGSize { Width = defaultSize, Height = defaultSize };
                });
                node.MarkDirtyAndPropagate();
            }
            return;
        }

        // No intrinsic measure: clear any previous function so a reused node
        // that stopped being measure-driven sizes as a plain flex box.
        if (state.HasMeasureFunc)
        {
            state.HasMeasureFunc = false;
            state.MeasureSig = null;
            state.MeasureKey = 0;
            node.SetMeasureFunc(null);
            node.MarkDirtyAndPropagate();
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
        if (panel.ComputedStyle.Display.Equals("none", StringComparison.OrdinalIgnoreCase) || panel.Children.Count == 0) return;
        var contentRight = 0f;
        var contentBottom = 0f;
        foreach (var child in panel.ChildrenInternal)
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

    private static void SetMargin(Node node, Edge edge, CssLength value) => node.Style.SetMargin(edge, ToLengthOrZero(value));

    private static void SetPadding(Node node, Edge edge, CssLength value) => node.Style.SetPadding(edge, ToLengthOrZero(value));

    private static void SetBorder(Node node, Edge edge, CssLength value) => node.Style.SetBorder(edge, ToLengthOrZero(value));

    private static StyleSizeLength ToSize(CssLength value) => value.Unit switch
    {
        CssLengthUnit.Points => StyleSizeLength.Points(value.Value),
        CssLengthUnit.Percent => StyleSizeLength.Percent(value.Value),
        CssLengthUnit.Auto => StyleSizeLength.OfAuto(),
        CssLengthUnit.MaxContent => StyleSizeLength.OfMaxContent(),
        CssLengthUnit.FitContent => StyleSizeLength.OfFitContent(),
        _ => StyleSizeLength.Undefined()
    };

    /// <summary>
    /// Maps a CSS length to a Yoga length, falling back to zero when undefined:
    /// Yoga.Net's length setters ignore undefined values, so a reused node must
    /// receive an explicit zero to clear a previously set edge.
    /// </summary>
    private static StyleLength ToLengthOrZero(CssLength value) => value.Unit switch
    {
        CssLengthUnit.Points => StyleLength.Points(value.Value),
        CssLengthUnit.Percent => StyleLength.Percent(value.Value),
        CssLengthUnit.Auto => StyleLength.OfAuto(),
        _ => StyleLength.Points(0f)
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
