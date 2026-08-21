using System.Numerics;
using Crowbar.FileSystems;
using Crowbar.UI;

namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// Paints a laid-out UI panel tree (<see cref="ScreenPanel"/>) into a
/// <see cref="Renderer2D"/>, replacing the Skia rasterizer. This is the wiring
/// between the UI framework and the framework-independent 2D renderer: the UI
/// exposes its panel tree and computed styles, and this painter emits the
/// <c>Draw*</c> commands in CSS paint order. It depends only on the public UI
/// surface (layout boxes, computed styles, panel subclasses), never on WebGPU.
/// </summary>
public sealed class UiTreePainter
{
    private readonly Renderer2D _renderer;

    // Asset resolution stays behind delegates so the painter is decoupled from
    // the asset pipeline. The editor supplies real loaders; tests inject
    // in-memory resolvers. Both caches are keyed so assets decode/parse once.
    private readonly Dictionary<string, SvgShape?> _svgCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image2D?> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BackdropRegion> _backdrops = [];
    private string? _tooltipText;
    private Vector2 _tooltipAnchor;

    private static readonly ColorF TooltipBackground = ColorF.FromRgba(24, 26, 30, 245);
    private static readonly ColorF TooltipBorder = ColorF.FromRgba(70, 76, 88, 255);
    private static readonly ColorF TooltipTextColor = ColorF.FromRgba(232, 235, 242, 255);

    public UiTreePainter(Renderer2D renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    /// <summary>The renderer this painter records into.</summary>
    public Renderer2D Renderer => _renderer;

    /// <summary>
    /// The backdrop-filter regions collected by the last paint, in paint order.
    /// The GPU compositor draws one region per quad between the 3D scene and the
    /// UI (sampling the scene texture).
    /// </summary>
    public IReadOnlyList<BackdropRegion> Backdrops => _backdrops;

    /// <summary>
    /// Resolves an <c>&lt;icon&gt;</c> name (the SVG file name without its
    /// extension) to a parsed <see cref="SvgShape"/>. Null falls back to the
    /// built-in loader reading <c>Assets/Icons/&lt;name&gt;.svg</c>.
    /// </summary>
    public Func<string, SvgShape?>? IconResolver { get; set; }

    /// <summary>
    /// Resolves an <c>&lt;img&gt;</c>/<c>background-image</c> source to an
    /// <see cref="Image2D"/>. Null falls back to <see cref="Renderer2D.LoadImage"/>.
    /// </summary>
    public Func<string, Image2D?>? ImageResolver { get; set; }

    /// <summary>
    /// Sets the hover tooltip to draw on top of the tree (screen-space cursor
    /// anchor), or null to hide it. Mirrors the old Skia renderer's tooltip.
    /// </summary>
    public void SetTooltip(string? text, Vector2 anchor)
    {
        _tooltipText = text;
        _tooltipAnchor = anchor;
    }

    /// <summary>
    /// Paints the whole tree at the root's viewport size, applying the root's
    /// scale. The renderer's frame is begun and ended here, so this is the only
    /// entry point a caller needs per frame.
    /// </summary>
    public void Paint(ScreenPanel root)
    {
        // Images register into the renderer's texture atlas, which must not
        // mutate while a frame is being recorded, so resolve them before Begin.
        PreResolveImages(root);
        _backdrops.Clear();

        var width = Math.Max(1, (int)MathF.Ceiling(root.Layout.Width * root.Scale));
        var height = Math.Max(1, (int)MathF.Ceiling(root.Layout.Height * root.Scale));
        _renderer.Begin(width, height);
        if (root.Scale != 1f)
            _renderer.PushScale(root.Scale);
        PaintPanel(root, Vector2.Zero, root.Opacity);
        if (root.Scale != 1f)
            _renderer.PopTransform();
        // The tooltip is an overlay in screen space, drawn above the tree.
        DrawTooltip(new Vector2(width, height));
        _renderer.End();
    }

    /// <summary>
    /// Paints the hover tooltip: a dark rounded box with a subtle border and the
    /// tooltip text in near-white, placed next to the cursor (flipping to the
    /// other side when it would overflow the viewport).
    /// </summary>
    private void DrawTooltip(Vector2 viewport)
    {
        var text = _tooltipText;
        if (string.IsNullOrEmpty(text))
            return;

        var style = new TextStyle(12f, TooltipTextColor, "Segoe UI", 400);
        var textWidth = _renderer.MeasureText(text, style);
        const float padX = 8f;
        const float padY = 5f;
        var boxWidth = textWidth + padX * 2f;
        var boxHeight = 12f + padY * 2f;
        var x = _tooltipAnchor.X + 14f;
        var y = _tooltipAnchor.Y + 18f;
        if (x + boxWidth > viewport.X) x = Math.Max(0f, _tooltipAnchor.X - boxWidth - 14f);
        if (y + boxHeight > viewport.Y) y = Math.Max(0f, _tooltipAnchor.Y - boxHeight - 18f);

        var rect = new RectF(x, y, boxWidth, boxHeight);
        _renderer.DrawRoundedRect(rect, 4f, TooltipBackground);
        _renderer.DrawRoundedRect(rect, 4f, 1f, TooltipBorder);
        _renderer.DrawText(text, new Vector2(x + padX, y + padY), style);
    }

    private void PreResolveImages(Panel panel)
    {
        var style = panel.ComputedStyle;
        if (!panel.IsVisible || style.Display.Equals("none", StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.IsNullOrEmpty(style.BackgroundImage))
            ResolveImage(style.BackgroundImage);
        if (panel is Image { Source: not null and not "" } image)
            ResolveImage(image.Source);
        foreach (var child in panel.Children)
            PreResolveImages(child);
    }

    private void PaintPanel(Panel panel, Vector2 origin, float opacity)
    {
        var style = panel.ComputedStyle;
        if (!panel.IsVisible || style.Display.Equals("none", StringComparison.OrdinalIgnoreCase))
            return;

        var rect = new RectF(
            panel.Layout.X + origin.X, panel.Layout.Y + origin.Y,
            panel.Layout.Width, panel.Layout.Height);
        var alpha = style.Opacity * opacity;

        // backdrop-filter: the backdrop itself is filtered by the GPU
        // compositor (Ui/Backdrop.slang) sampling the 3D scene, not by the painter.
        // Transformed panels bypass this path (mirroring the Skia renderer).
        if (!style.HasTransform && !style.BackdropFilter.IsNone && CssFilterFunctions.IsGpuBackdropExpressible(style.BackdropFilter))
        {
            _backdrops.Add(new BackdropRegion(
                rect.X, rect.Y, rect.Width, rect.Height,
                style.BorderRadius, alpha, style.BackgroundColor, style.BackdropFilter));
        }

        if (style.HasTransform)
        {
            // The matrix maps the panel's local box (0,0,w,h) onto its global
            // position, pivoting on the resolved transform-origin. Children are
            // drawn inside that local space, so they inherit the transform.
            var matrix = BuildMatrix(style.Transform, rect.Width, rect.Height, style.TransformOrigin, rect.X, rect.Y);
            _renderer.PushTransform(matrix);
            var localOrigin = new Vector2(-panel.Layout.X, -panel.Layout.Y);
            PaintContent(panel, new RectF(0, 0, rect.Width, rect.Height), alpha, localOrigin);
            _renderer.PopTransform();
            return;
        }

        var filter = ToFilter(style.Filter);
        if (!filter.IsNone)
        {
            // The panel's own rendering (background, text, children) is drawn
            // into an offscreen layer and composited back through the filter,
            // matching the CSS group semantics.
            _renderer.PushFilter(filter);
            PaintContent(panel, rect, alpha, origin);
            _renderer.PopFilter();
        }
        else
        {
            PaintContent(panel, rect, alpha, origin);
        }
    }

    private void PaintContent(Panel panel, RectF rect, float alpha, Vector2 origin)
    {
        var style = panel.ComputedStyle;

        DrawBoxShadows(panel, rect, alpha, inset: false);
        DrawBackground(panel, rect, alpha);
        DrawBackgroundImage(panel, rect, alpha);
        DrawBoxShadows(panel, rect, alpha, inset: true);
        DrawBorders(panel, rect, alpha);
        if (panel is Image { Source: not null and not "" } image)
            DrawImageContent(panel, image.Source, rect, alpha);
        if (panel is Icon { Name: not null and not "" } icon)
            DrawIconContent(icon.Name, rect, alpha, panel);
        if (panel is ToggleInput toggle)
            DrawToggle(panel, rect, alpha, toggle);

        var input = panel as TextInput;
        var text = panel.TagName == "text" ? panel.Text : input?.Value ?? string.Empty;
        var isPlaceholder = input is not null && text.Length == 0 && input.Placeholder.Length > 0;
        if (!string.IsNullOrEmpty(text) || isPlaceholder)
            DrawText(panel, rect, isPlaceholder ? input!.Placeholder : text, alpha, isPlaceholder);
        // Generated ::before/::after content of non-text panels paints as a
        // decorative line at the content box start (before) / end (after), using
        // the pseudo element's own computed style.
        if (panel.TagName != "text" && panel is not TextInput)
        {
            if (panel.PseudoBefore is { } pseudoBefore)
                DrawPseudoText(panel, rect, pseudoBefore.Style, pseudoBefore.Text, start: true, alpha);
            if (panel.PseudoAfter is { } pseudoAfter)
                DrawPseudoText(panel, rect, pseudoAfter.Style, pseudoAfter.Text, start: false, alpha);
        }

        if (panel.ClipsContent)
        {
            var border = panel.LayoutBorder;
            var clip = new RectF(
                rect.X + border.Left, rect.Y + border.Top,
                Math.Max(0, rect.Width - border.Left - border.Right),
                Math.Max(0, rect.Height - border.Top - border.Bottom));
            _renderer.PushClip(clip);
            DrawChildren(panel, origin, alpha);
            _renderer.PopClip();
        }
        else
        {
            DrawChildren(panel, origin, alpha);
        }

        DrawScrollBars(panel, origin);
        DrawOutline(panel, rect, alpha);
    }

    private void DrawChildren(Panel panel, Vector2 origin, float alpha)
    {
        var children = panel.Children;
        if (children.Count <= 1 || !children.Any(child => child.ComputedStyle.ZIndex != 0))
        {
            foreach (var child in children)
                PaintPanel(child, new Vector2(origin.X - panel.ScrollX, origin.Y - panel.ScrollY), alpha);
            return;
        }

        foreach (var child in children.OrderBy(child => child.ComputedStyle.ZIndex))
            PaintPanel(child, new Vector2(origin.X - panel.ScrollX, origin.Y - panel.ScrollY), alpha);
    }

    // ---------------------------------------------------------------------
    // Backgrounds and shadows
    // ---------------------------------------------------------------------

    private static float EffectiveRadius(Panel panel, RectF rect) =>
        Math.Min(panel.ComputedStyle.BorderRadius, Math.Min(rect.Width, rect.Height) / 2f);

    private void DrawBackground(Panel panel, RectF rect, float alpha)
    {
        var background = panel.ComputedStyle.BackgroundColor;
        if (background.A == 0)
            return;
        _renderer.DrawRoundedRect(rect, EffectiveRadius(panel, rect), ToColorF(background, alpha));
    }

    private void DrawBackgroundImage(Panel panel, RectF rect, float alpha)
    {
        var style = panel.ComputedStyle;
        var source = style.BackgroundImage;
        if (string.IsNullOrEmpty(source))
            return;
        var image = ResolveImage(source);
        if (image is null)
            return;

        // The background positioning area is the padding box, clipped to the
        // border-box rounded shape.
        var border = panel.LayoutBorder;
        var padding = panel.LayoutPadding;
        var area = new RectF(
            rect.X + border.Left + padding.Left, rect.Y + border.Top + padding.Top,
            Math.Max(0, rect.Width - border.Left - border.Right - padding.Left - padding.Right),
            Math.Max(0, rect.Height - border.Top - border.Bottom - padding.Top - padding.Bottom));
        if (area.Width <= 0 || area.Height <= 0)
            return;

        _renderer.PushClip(rect, EffectiveRadius(panel, rect));

        // cover/contain scale one image to the whole area (never tiled).
        if (style.BackgroundSize.Type is BackgroundSizeType.Cover or BackgroundSizeType.Contain)
        {
            var fit = style.BackgroundSize.Type == BackgroundSizeType.Cover ? ImageFit.Cover : ImageFit.Contain;
            _renderer.DrawImage(area, image, ColorF.White.WithAlpha(alpha), fit);
            _renderer.PopClip();
            return;
        }

        var (tileW, tileH) = ResolveTileSize(style.BackgroundSize, image, area);
        if (tileW <= 0 || tileH <= 0)
        {
            _renderer.PopClip();
            return;
        }

        // The reference tile's top-left comes from background-position (a
        // percentage offsets by (area - tile) * pct). Repeating tiles fill both
        // directions from that reference; no-repeat draws a single tile.
        var noRepeatX = style.BackgroundRepeat.X == RepeatMode.NoRepeat;
        var noRepeatY = style.BackgroundRepeat.Y == RepeatMode.NoRepeat;
        var tileX0 = area.X + ResolvePosition(style.BackgroundPosition.X, area.Width, tileW);
        var tileY0 = area.Y + ResolvePosition(style.BackgroundPosition.Y, area.Height, tileH);
        var startX = noRepeatX ? tileX0 : tileX0 + MathF.Floor((area.X - tileX0) / tileW) * tileW;
        var startY = noRepeatY ? tileY0 : tileY0 + MathF.Floor((area.Y - tileY0) / tileH) * tileH;

        var color = ColorF.White.WithAlpha(alpha);
        var y = startY;
        while (y < area.Bottom)
        {
            var x = startX;
            while (x < area.Right)
            {
                _renderer.DrawImage(new RectF(x, y, tileW, tileH), image, color, ImageFit.Stretch);
                if (noRepeatX)
                    break;
                x += tileW;
            }
            if (noRepeatY)
                break;
            y += tileH;
        }

        _renderer.PopClip();
    }

    /// <summary>Resolves the tiled background size: explicit lengths, or the image's intrinsic size otherwise.</summary>
    private static (float Width, float Height) ResolveTileSize(BackgroundSize size, Image2D image, RectF area)
    {
        var iw = (float)image.Width;
        var ih = (float)image.Height;
        if (iw <= 0 || ih <= 0)
            return (area.Width, area.Height);

        if (size.Type != BackgroundSizeType.Explicit)
            return (iw, ih);

        var w = ResolveExplicitLength(size.Width, area.Width);
        var h = ResolveExplicitLength(size.Height, area.Height);
        // A single explicit axis scales the other to preserve the ratio; a
        // fully-auto pair falls back to the intrinsic size.
        if (w <= 0 && h <= 0)
            return (iw, ih);
        if (w <= 0)
            w = h * iw / ih;
        if (h <= 0)
            h = w * ih / iw;
        return (w, h);
    }

    /// <summary>Resolves an explicit background-size length (points/percent) to pixels, 0 for auto.</summary>
    private static float ResolveExplicitLength(CssLength length, float reference) => length.Unit switch
    {
        CssLengthUnit.Points => length.Value,
        CssLengthUnit.Percent => length.Value / 100f * reference,
        _ => 0f
    };

    /// <summary>Resolves a background-position component to a pixel offset inside the area.</summary>
    private static float ResolvePosition(CssLength length, float area, float tile) => length.Unit switch
    {
        CssLengthUnit.Points => length.Value,
        CssLengthUnit.Percent => (area - tile) * length.Value / 100f,
        _ => 0f
    };

    private void DrawBoxShadows(Panel panel, RectF rect, float alpha, bool inset)
    {
        var shadows = panel.ComputedStyle.BoxShadows;
        if (shadows.Length == 0)
            return;
        var radius = EffectiveRadius(panel, rect);
        foreach (var shadow in shadows)
        {
            if (shadow.Inset != inset)
                continue;
            var color = ToColorF(shadow.Color, alpha);
            if (color.A <= 0f)
                continue;
            var offset = new Vector2(shadow.OffsetX, shadow.OffsetY);
            if (inset)
                _renderer.DrawInnerShadow(rect, radius, offset, shadow.BlurRadius, shadow.SpreadRadius, color);
            else
                _renderer.DrawBoxShadow(rect, radius, offset, shadow.BlurRadius, shadow.SpreadRadius, color);
        }
    }

    // ---------------------------------------------------------------------
    // Borders and outlines
    // ---------------------------------------------------------------------

    private static BorderStyle? ToBorderStyle(string name) => name.ToLowerInvariant() switch
    {
        "solid" => BorderStyle.Solid,
        "dashed" => BorderStyle.Dashed,
        "dotted" => BorderStyle.Dotted,
        "double" => BorderStyle.Double,
        // groove/ridge/inset/outset render as a solid ring (per-side shading deferred).
        "groove" or "ridge" or "inset" or "outset" => BorderStyle.Solid,
        _ => null
    };

    private void DrawBorders(Panel panel, RectF rect, float alpha)
    {
        var style = panel.ComputedStyle;
        var widths = panel.LayoutBorder;
        var top = style.BorderTopStyle;
        var right = style.BorderRightStyle;
        var bottom = style.BorderBottomStyle;
        var left = style.BorderLeftStyle;

        var uniformStyle = top == right && right == bottom && bottom == left;
        var uniformColor = style.BorderTopColor == style.BorderRightColor &&
                           style.BorderRightColor == style.BorderBottomColor &&
                           style.BorderBottomColor == style.BorderLeftColor;
        var uniformWidth = MathF.Abs(widths.Top - widths.Right) < 0.001f &&
                           MathF.Abs(widths.Right - widths.Bottom) < 0.001f &&
                           MathF.Abs(widths.Bottom - widths.Left) < 0.001f;

        if (uniformStyle && uniformColor && uniformWidth)
        {
            var borderStyle = ToBorderStyle(top);
            if (borderStyle is null || widths.Top <= 0f)
                return;
            var half = widths.Top / 2f;
            var insetRect = rect.Inflate(-half);
            var radius = Math.Max(0f, EffectiveRadius(panel, rect) - half);
            var dash = top.Equals("dashed", StringComparison.OrdinalIgnoreCase) ? 3f * widths.Top
                : top.Equals("dotted", StringComparison.OrdinalIgnoreCase) ? widths.Top
                : 8f;
            _renderer.DrawRoundedRect(insetRect, radius, widths.Top, ToColorF(style.BorderTopColor, alpha), borderStyle.Value, dash);
            return;
        }

        // Non-uniform borders: solid per-side bands (dashed/dotted/double and
        // rounded-corner carving per side are deferred to the follow-up).
        DrawBorderBand(rect, widths.Top, top, style.BorderTopColor, alpha, side: Side.Top);
        DrawBorderBand(rect, widths.Right, right, style.BorderRightColor, alpha, side: Side.Right);
        DrawBorderBand(rect, widths.Bottom, bottom, style.BorderBottomColor, alpha, side: Side.Bottom);
        DrawBorderBand(rect, widths.Left, left, style.BorderLeftColor, alpha, side: Side.Left);
    }

    private enum Side { Top, Right, Bottom, Left }

    private void DrawBorderBand(RectF rect, float width, string styleName, UiColor color, float alpha, Side side)
    {
        if (width <= 0f || styleName is "none" or "hidden")
            return;
        var band = side switch
        {
            Side.Top => new RectF(rect.X, rect.Y, rect.Width, width),
            Side.Right => new RectF(rect.Right - width, rect.Y, width, rect.Height),
            Side.Bottom => new RectF(rect.X, rect.Bottom - width, rect.Width, width),
            _ => new RectF(rect.X, rect.Y, width, rect.Height)
        };
        _renderer.DrawRect(band, ToColorF(color, alpha));
    }

    private void DrawOutline(Panel panel, RectF rect, float alpha)
    {
        var style = panel.ComputedStyle;
        if (style.OutlineStyle is "none" or "hidden" || style.OutlineWidth <= 0f)
            return;
        var borderStyle = ToBorderStyle(style.OutlineStyle);
        if (borderStyle is null)
            return;
        var half = style.OutlineWidth / 2f;
        var outer = rect.Inflate(style.OutlineOffset + half);
        var corner = Math.Max(0f, style.BorderRadius + style.OutlineOffset + half);
        _renderer.DrawRoundedRect(outer, corner, style.OutlineWidth, ToColorF(style.OutlineColor, alpha), borderStyle.Value);
    }

    // ---------------------------------------------------------------------
    // Content: images, icons, toggles, text
    // ---------------------------------------------------------------------

    private void DrawImageContent(Panel panel, string source, RectF rect, float alpha)
    {
        var image = ResolveImage(source);
        if (image is null)
            return;

        var border = panel.LayoutBorder;
        var padding = panel.LayoutPadding;
        var content = new RectF(
            rect.X + border.Left + padding.Left, rect.Y + border.Top + padding.Top,
            Math.Max(0, rect.Width - border.Left - border.Right - padding.Left - padding.Right),
            Math.Max(0, rect.Height - border.Top - border.Bottom - padding.Top - padding.Bottom));

        var fit = panel.ComputedStyle.ObjectFit.ToLowerInvariant() switch
        {
            "contain" => ImageFit.Contain,
            "cover" => ImageFit.Cover,
            "none" or "scale-down" => ImageFit.Center,
            _ => ImageFit.Stretch
        };

        _renderer.PushClip(rect, EffectiveRadius(panel, rect));
        _renderer.DrawImage(content, image, ColorF.White.WithAlpha(alpha), fit);
        _renderer.PopClip();
    }

    private void DrawIconContent(string name, RectF rect, float alpha, Panel panel)
    {
        var svg = ResolveIcon(name);
        if (svg is null)
            return;

        var border = panel.LayoutBorder;
        var padding = panel.LayoutPadding;
        var content = new RectF(
            rect.X + border.Left + padding.Left, rect.Y + border.Top + padding.Top,
            Math.Max(0, rect.Width - border.Left - border.Right - padding.Left - padding.Right),
            Math.Max(0, rect.Height - border.Top - border.Bottom - padding.Top - padding.Bottom));

        // The tint comes from the computed color, like text (hover/active
        // states fall out of the CSS color pipeline).
        _renderer.PushClip(rect, EffectiveRadius(panel, rect));
        _renderer.DrawSvg(svg, content, ToColorF(panel.ComputedStyle.Color, alpha));
        _renderer.PopClip();
    }

    private void DrawToggle(Panel panel, RectF rect, float alpha, ToggleInput toggle)
    {
        var style = panel.ComputedStyle;
        var border = panel.LayoutBorder;
        var padding = panel.LayoutPadding;
        var size = Math.Max(12, Math.Min(16,
            Math.Min(rect.Width - border.Left - border.Right - padding.Left - padding.Right,
                rect.Height - border.Top - border.Bottom - padding.Top - padding.Bottom)));
        if (size <= 0)
            return;

        var x = rect.X + border.Left + padding.Left;
        var y = rect.Y + border.Top + padding.Top;
        var dim = panel.IsEnabled ? 1f : 0.5f;
        var box = new RectF(x, y, size, size);
        var fill = ColorF.White.WithAlpha(alpha * dim);
        var outline = ColorF.FromRgba(90, 90, 100).WithAlpha(alpha * dim);
        var accent = (panel.IsChecked
            ? ColorF.FromRgba(40, 100, 220)
            : ColorF.FromRgba(120, 120, 130)).WithAlpha(alpha * dim);

        if (toggle.IsRadio)
        {
            _renderer.DrawCircle(box.Center, size / 2f, fill);
            _renderer.DrawCircle(box.Center, size / 2f, 1.5f, outline);
            if (panel.IsChecked)
                _renderer.DrawCircle(box.Center, size / 2f - 2.5f, accent);
        }
        else
        {
            var radius = Math.Min(4f, size / 4f);
            _renderer.DrawRoundedRect(box, radius, fill);
            _renderer.DrawRoundedRect(box, radius, 1.5f, outline);
            if (panel.IsChecked)
            {
                var width = Math.Max(1.5f, size / 8f);
                _renderer.DrawLine(new Vector2(x + size * 0.2f, y + size * 0.52f), new Vector2(x + size * 0.42f, y + size * 0.7f), width, accent);
                _renderer.DrawLine(new Vector2(x + size * 0.42f, y + size * 0.7f), new Vector2(x + size * 0.82f, y + size * 0.3f), width, accent);
            }
        }
    }

    private void DrawText(Panel panel, RectF rect, string text, float alpha, bool isPlaceholder = false)
    {
        var style = panel.ComputedStyle;
        var padding = panel.LayoutPadding;
        var left = rect.X + padding.Left;
        var top = rect.Y + padding.Top;
        var contentWidth = Math.Max(0, rect.Width - padding.Left - padding.Right);
        var contentHeight = Math.Max(0, rect.Height - padding.Top - padding.Bottom);

        // ::before/::after content of a text panel joins the text as one flow
        // (the layout box already measured it the same way).
        var displayText = panel is TextInput && !isPlaceholder || (panel.PseudoBefore is null && panel.PseudoAfter is null)
            ? text
            : (panel.PseudoBefore?.Text ?? string.Empty) + text + (panel.PseudoAfter?.Text ?? string.Empty);
        var transformed = ApplyTextTransform(displayText, style.TextTransform);
        if (string.IsNullOrEmpty(transformed))
            return;

        var lineHeight = style.LineHeight > 0 ? style.LineHeight : style.FontSize * 1.25f;
        // Vertical centering uses a single-line estimate (multi-line center is
        // deferred); inputs and buttons are the common vertical-align users and
        // they are single-line.
        var y = style.VerticalAlign.Equals("center", StringComparison.OrdinalIgnoreCase)
            ? top + Math.Max(0, (contentHeight - lineHeight) / 2f)
            : style.VerticalAlign.Equals("bottom", StringComparison.OrdinalIgnoreCase)
                ? top + Math.Max(0, contentHeight - lineHeight)
                : top;

        var align = style.TextAlign.Equals("center", StringComparison.OrdinalIgnoreCase) ? TextAlign.Center
            : style.TextAlign.Equals("right", StringComparison.OrdinalIgnoreCase) ? TextAlign.Right
            : TextAlign.Left;
        var color = ToColorF(style.Color, alpha);

        // nowrap/pre (and inputs) never wrap; otherwise newlines split lines.
        var wrap = style.WhiteSpace is "nowrap" or "pre" ? 0f : contentWidth;
        var singleLine = wrap == 0f || !transformed.Contains('\n');

        // Selection highlight and caret are placed with the same measurement the
        // renderer uses for alignment, so they track the glyphs exactly.
        var measureStyle = new TextStyle(style.FontSize, color, style.FontFamily, style.FontWeight, style.LetterSpacing);
        var measured = singleLine ? _renderer.MeasureText(transformed, measureStyle) : 0f;
        var x = align == TextAlign.Center ? left + Math.Max(0, (contentWidth - measured) / 2f)
            : align == TextAlign.Right ? left + Math.Max(0, contentWidth - measured)
            : left;

        if (!isPlaceholder && panel is TextInput input && input.HasSelection && singleLine)
        {
            var start = Math.Clamp(Math.Min(input.SelectionStart, input.SelectionEnd), 0, transformed.Length);
            var end = Math.Clamp(Math.Max(input.SelectionStart, input.SelectionEnd), 0, transformed.Length);
            var selLeft = x + _renderer.MeasureText(transformed[..start], measureStyle);
            var selRight = x + _renderer.MeasureText(transformed[..end], measureStyle);
            _renderer.DrawRect(new RectF(selLeft, y, Math.Max(0, selRight - selLeft), lineHeight), ColorF.FromRgba(50, 120, 220).WithAlpha(alpha));
        }

        // text-overflow: ellipsis truncates the painted line to the content box.
        // The layout pass measured it the same way (a trailing ellipsis), but
        // SixLabors only wraps and never ellipsizes, so the raw string would
        // spill past the clipped box. Truncate before drawing so the glyphs
        // match the measured layout box.
        var drawText = singleLine && contentWidth > 0 &&
            style.TextOverflow.Equals("ellipsis", StringComparison.OrdinalIgnoreCase)
            ? Ellipsize(transformed, contentWidth, measureStyle)
            : transformed;

        // A nowrap line has no wrapping box for the text renderer to align within,
        // so use the measured position calculated above and render it as a left-
        // aligned run. Wrapped text keeps the content-box alignment handled by
        // the renderer.
        var drawX = wrap == 0f ? x : left;
        var drawAlign = wrap == 0f ? TextAlign.Left : align;
        var textStyle = new TextStyle(
            style.FontSize, color, style.FontFamily, style.FontWeight,
            style.LetterSpacing, wrap, lineHeight, drawAlign);
        if (style.TextShadows.Length > 0)
        {
            var shadow = style.TextShadows[0];
            textStyle = textStyle.WithShadow(
                new Vector2(shadow.OffsetX, shadow.OffsetY), shadow.BlurRadius, ToColorF(shadow.Color, alpha));
        }
        _renderer.DrawText(drawText, new Vector2(drawX, y), textStyle);

        if (panel is TextInput caretInput && caretInput.IsFocused && caretInput.CaretVisible && singleLine)
        {
            var caretX = x + _renderer.MeasureText(transformed[..Math.Clamp(caretInput.CaretIndex, 0, transformed.Length)], measureStyle);
            _renderer.DrawLine(
                new Vector2(caretX, top + 3),
                new Vector2(caretX, top + Math.Max(style.FontSize + 3, contentHeight - 3)),
                1.5f, color);
        }
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to fit <paramref name="maxWidth"/>,
    /// appending a single ellipsis character — the same truncation the layout
    /// measure applies, so the painted glyphs and the layout box stay in
    /// agreement instead of overflowing the clipped box.
    /// </summary>
    private string Ellipsize(string text, float maxWidth, in TextStyle measureStyle)
    {
        if (_renderer.MeasureText(text, measureStyle) <= maxWidth) return text;
        const string dots = "\u2026";
        var truncated = text;
        while (truncated.Length > 0 && _renderer.MeasureText(truncated + dots, measureStyle) > maxWidth)
            truncated = truncated[..^1];
        return truncated + dots;
    }

    /// <summary>
    /// Draws the generated content of a <c>::before</c>/<c>::after</c> element
    /// of a non-text panel: a single line at the content box start (top-left) or
    /// end, styled by the pseudo element's own computed style.
    /// </summary>
    private void DrawPseudoText(Panel panel, RectF rect, ComputedStyle style, string text, bool start, float alpha)
    {
        var border = panel.LayoutBorder;
        var padding = panel.LayoutPadding;
        var left = rect.X + border.Left + padding.Left;
        var top = rect.Y + border.Top + padding.Top;
        var contentHeight = Math.Max(0, rect.Height - border.Top - border.Bottom - padding.Top - padding.Bottom);
        var transformed = ApplyTextTransform(text, style.TextTransform);
        if (string.IsNullOrEmpty(transformed))
            return;
        var lineHeight = style.LineHeight > 0 ? style.LineHeight : style.FontSize * 1.25f;
        var y = start ? top : top + Math.Max(0, contentHeight - lineHeight);
        var textStyle = new TextStyle(
            style.FontSize, ToColorF(style.Color, alpha), style.FontFamily, style.FontWeight,
            style.LetterSpacing, 0f, lineHeight, TextAlign.Left);
        _renderer.DrawText(transformed, new Vector2(left, y), textStyle);
    }

    private void DrawScrollBars(Panel panel, Vector2 origin)
    {
        var style = panel.ComputedStyle;
        var radius = Math.Min(style.ScrollbarRadius, panel.ScrollbarThickness / 2f);
        var trackColor = ToColorF(style.ScrollbarTrackColor, 1f);
        var thumbColor = ToColorF(style.ScrollbarThumbColor, 1f);

        if (ScrollBars.ShouldShowVertical(panel))
        {
            var track = ScrollBars.VerticalTrack(panel);
            _renderer.DrawRoundedRect(OffsetRect(track, origin), radius, trackColor);
            var thumb = ScrollBars.VerticalThumb(panel);
            _renderer.DrawRoundedRect(OffsetRect(thumb, origin), radius, thumbColor);
        }

        if (ScrollBars.ShouldShowHorizontal(panel))
        {
            var track = ScrollBars.HorizontalTrack(panel);
            _renderer.DrawRoundedRect(OffsetRect(track, origin), radius, trackColor);
            var thumb = ScrollBars.HorizontalThumb(panel);
            _renderer.DrawRoundedRect(OffsetRect(thumb, origin), radius, thumbColor);
        }
    }

    // ---------------------------------------------------------------------
    // Conversions
    // ---------------------------------------------------------------------

    private static ColorF ToColorF(UiColor color, float alpha) =>
        ColorF.FromRgba(color.R, color.G, color.B, (byte)Math.Clamp(color.A * alpha, 0, 255));

    private static Filter2D ToFilter(CssFilter filter)
    {
        if (filter.IsNone)
            return Filter2D.None;
        var ops = new List<FilterOp>(filter.Functions.Count);
        foreach (var function in filter.Functions)
        {
            if (function.Parameters.Count == 0)
                continue;
            var amount = function.Parameters[0];
            switch (function.Name.ToLowerInvariant())
            {
                case "blur": ops.Add(FilterOp.Blur(amount)); break;
                case "brightness": ops.Add(FilterOp.Brightness(amount)); break;
                case "contrast": ops.Add(FilterOp.Contrast(amount)); break;
                case "grayscale": ops.Add(FilterOp.Grayscale(amount)); break;
                case "hue-rotate": ops.Add(FilterOp.HueRotate(amount)); break;
                case "invert": ops.Add(FilterOp.Invert(amount)); break;
                case "opacity": ops.Add(FilterOp.Opacity(amount)); break;
                case "saturate": ops.Add(FilterOp.Saturate(amount)); break;
                case "sepia": ops.Add(FilterOp.Sepia(amount)); break;
                // drop-shadow and unknown functions are not expressible as a
                // Filter2D yet; they are dropped (documented limitation).
            }
        }
        return Filter2D.Create(ops);
    }

    /// <summary>Applies <c>text-transform</c> (none, uppercase, lowercase, capitalize).</summary>
    internal static string ApplyTextTransform(string text, string transform)
    {
        switch (transform.ToLowerInvariant())
        {
            case "uppercase": return text.ToUpperInvariant();
            case "lowercase": return text.ToLowerInvariant();
            case "capitalize":
            {
                var sb = new System.Text.StringBuilder(text.Length);
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

    /// <summary>Builds the local→global affine matrix for a transform, pivoting on the resolved origin.</summary>
    private static Matrix3x2 BuildMatrix(TransformList transform, float width, float height, TransformOrigin origin, float offsetX, float offsetY)
    {
        var ox = origin.ResolveX(width);
        var oy = origin.ResolveY(height);
        var m = Matrix3x2.CreateTranslation(-ox, -oy);
        foreach (var op in transform.Ops)
            m = OpMatrix(op, width, height) * m;
        return Matrix3x2.CreateTranslation(offsetX + ox, offsetY + oy) * m;
    }

    private static Matrix3x2 OpMatrix(TransformOp op, float width, float height) => op.Type switch
    {
        TransformOpType.Matrix => new Matrix3x2(op.A, op.B, op.C, op.D, op.E, op.F),
        TransformOpType.Translate => Matrix3x2.CreateTranslation(
            op.AIsPercent ? width * op.A / 100f : op.A,
            op.BIsPercent ? height * op.B / 100f : op.B),
        TransformOpType.TranslateX => Matrix3x2.CreateTranslation(op.AIsPercent ? width * op.A / 100f : op.A, 0f),
        TransformOpType.TranslateY => Matrix3x2.CreateTranslation(0f, op.AIsPercent ? height * op.A / 100f : op.A),
        TransformOpType.Scale => Matrix3x2.CreateScale(op.A, op.B),
        TransformOpType.ScaleX => Matrix3x2.CreateScale(op.A, 1f),
        TransformOpType.ScaleY => Matrix3x2.CreateScale(1f, op.A),
        TransformOpType.Rotate => Matrix3x2.CreateRotation(op.A * MathF.PI / 180f),
        TransformOpType.Skew => SkewMatrix(op.A, op.B),
        TransformOpType.SkewX => SkewMatrix(op.A, 0f),
        TransformOpType.SkewY => SkewMatrix(0f, op.A),
        _ => Matrix3x2.Identity
    };

    private static Matrix3x2 SkewMatrix(float xDegrees, float yDegrees) =>
        new(1f, MathF.Tan(yDegrees * MathF.PI / 180f), MathF.Tan(xDegrees * MathF.PI / 180f), 1f, 0f, 0f);

    private static RectF OffsetRect(UiRect rect, Vector2 origin) =>
        new(rect.X + origin.X, rect.Y + origin.Y, rect.Width, rect.Height);

    // ---------------------------------------------------------------------
    // Asset resolution
    // ---------------------------------------------------------------------

    private SvgShape? ResolveIcon(string name)
    {
        if (_svgCache.TryGetValue(name, out var cached))
            return cached;
        var resolved = IconResolver is not null ? IconResolver(name) : DefaultIconLoader(name);
        _svgCache[name] = resolved;
        return resolved;
    }

    private Image2D? ResolveImage(string source)
    {
        if (_imageCache.TryGetValue(source, out var cached))
            return cached;
        var resolved = ImageResolver is not null ? ImageResolver(source) : DefaultImageLoader(source);
        _imageCache[source] = resolved;
        return resolved;
    }

    private static string _iconRoot = PathUtil.Combine("Assets", "Icons");

    /// <summary>Directory icon names are resolved against (defaults to <c>Assets/Icons</c>).</summary>
    public static string IconRoot
    {
        get => _iconRoot;
        set => _iconRoot = value;
    }

    private static SvgShape? DefaultIconLoader(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains("..", StringComparison.Ordinal))
            return null;
        var path = PathUtil.Combine(IconRoot, normalized + ".svg");
        var fs = FileSystem.Content;
        if (!fs.FileExists(path))
            return null;
        try
        {
            // Packs hardcode concrete hex fills/strokes (the Solar pack):
            // normalize them to currentColor so the whole pack tints through
            // the computed color, exactly like SvgIconCache did with Skia.
            var source = System.Text.RegularExpressions.Regex.Replace(
                    fs.ReadAllText(path), "(fill|stroke)=\"#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?\"", "$1=\"currentColor\"");
            return SvgDocumentParser.Parse(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException)
        {
            return null;
        }
    }

    private Image2D? DefaultImageLoader(string source)
    {
        try
        {
            return _renderer.LoadImage(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
