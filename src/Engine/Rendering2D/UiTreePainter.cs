using System.Numerics;
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

    public UiTreePainter(Renderer2D renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    /// <summary>The renderer this painter records into.</summary>
    public Renderer2D Renderer => _renderer;

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
    /// Paints the whole tree at the root's viewport size, applying the root's
    /// scale. The renderer's frame is begun and ended here, so this is the only
    /// entry point a caller needs per frame.
    /// </summary>
    public void Paint(ScreenPanel root)
    {
        // Images register into the renderer's texture atlas, which must not
        // mutate while a frame is being recorded, so resolve them before Begin.
        PreResolveImages(root);

        var width = Math.Max(1, (int)MathF.Ceiling(root.Layout.Width * root.Scale));
        var height = Math.Max(1, (int)MathF.Ceiling(root.Layout.Height * root.Scale));
        _renderer.Begin(width, height);
        if (root.Scale != 1f)
            _renderer.PushScale(root.Scale);
        PaintPanel(root, Vector2.Zero, root.Opacity);
        if (root.Scale != 1f)
            _renderer.PopTransform();
        _renderer.End();
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

        var text = panel.TagName == "text" ? panel.Text : panel is TextInput input ? input.Value : string.Empty;
        if (!string.IsNullOrEmpty(text))
            DrawText(panel, rect, text, alpha);

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
        var source = panel.ComputedStyle.BackgroundImage;
        if (string.IsNullOrEmpty(source))
            return;
        var image = ResolveImage(source);
        if (image is null)
            return;

        // The background positioning area is the padding box, clipped to the
        // border-box rounded shape. Tiling/repeat is deferred: a single tile is
        // drawn fitted to the area (cover/contain/stretch).
        var border = panel.LayoutBorder;
        var padding = panel.LayoutPadding;
        var area = new RectF(
            rect.X + border.Left + padding.Left, rect.Y + border.Top + padding.Top,
            Math.Max(0, rect.Width - border.Left - border.Right - padding.Left - padding.Right),
            Math.Max(0, rect.Height - border.Top - border.Bottom - padding.Top - padding.Bottom));

        var fit = panel.ComputedStyle.BackgroundSize.Type switch
        {
            BackgroundSizeType.Cover => ImageFit.Cover,
            BackgroundSizeType.Contain => ImageFit.Contain,
            _ => ImageFit.Stretch
        };

        _renderer.PushClip(rect, EffectiveRadius(panel, rect));
        _renderer.DrawImage(area, image, ColorF.White.WithAlpha(alpha), fit);
        _renderer.PopClip();
    }

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

    private void DrawText(Panel panel, RectF rect, string text, float alpha)
    {
        var style = panel.ComputedStyle;
        var padding = panel.LayoutPadding;
        var left = rect.X + padding.Left;
        var top = rect.Y + padding.Top;
        var contentWidth = Math.Max(0, rect.Width - padding.Left - padding.Right);
        var contentHeight = Math.Max(0, rect.Height - padding.Top - padding.Bottom);

        var transformed = ApplyTextTransform(text, style.TextTransform);
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

        var textStyle = new TextStyle(
            style.FontSize,
            ToColorF(style.Color, alpha),
            style.FontFamily,
            style.FontWeight,
            style.LetterSpacing,
            contentWidth,
            lineHeight,
            align);

        if (style.TextShadows.Length > 0)
        {
            var shadow = style.TextShadows[0];
            textStyle = textStyle.WithShadow(
                new Vector2(shadow.OffsetX, shadow.OffsetY), shadow.BlurRadius, ToColorF(shadow.Color, alpha));
        }

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

    private static string _iconRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons");

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
        var path = Path.Combine(IconRoot, normalized + ".svg");
        if (!File.Exists(path))
            return null;
        try
        {
            // Packs hardcode concrete hex fills/strokes (the Solar pack):
            // normalize them to currentColor so the whole pack tints through
            // the computed color, exactly like SvgIconCache did with Skia.
            var source = System.Text.RegularExpressions.Regex.Replace(
                    File.ReadAllText(path), "(fill|stroke)=\"#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?\"", "$1=\"currentColor\"");
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
