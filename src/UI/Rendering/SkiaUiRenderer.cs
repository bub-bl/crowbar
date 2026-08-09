using SkiaSharp;

namespace Crowbar.UI;

public interface ICanvas
{
    void Clear(UiColor color);
    void FillRoundedRect(UiRect rect, float radius, UiColor color);
    void DrawText(string text, float x, float y, float size, UiColor color);
}

public interface IUiRenderer
{
    UiSize Size { get; }
    ReadOnlyMemory<byte> Render(ScreenPanel root);
    bool IsDirty { get; }
}

/// <summary>
/// A backdrop-filter region collected during the Skia raster pass. The region
/// itself is <em>not</em> rasterized by Skia: the GPU compositor (Backdrop.wgsl)
/// samples the WebGPU 3D viewport behind the UI, filters it and writes it into
/// this border box, exactly like S&amp;box's ui_backdropfilter shader. The panel's
/// own background and children are still painted by Skia and composite on top.
/// </summary>
public readonly record struct BackdropRegion(
    float X, float Y, float Width, float Height, float Radius, float Alpha, UiColor Tint, CssFilter Filter);

public sealed class SkiaUiRenderer : IUiRenderer, IDisposable
{
    private readonly YogaLayoutEngine _layout = new();
    private SKBitmap? _bitmap;
    private SKSurface? _surface;
    private byte[] _pixels = [];
    private bool _dirty = true;
    private readonly List<BackdropRegion> _backdrops = [];

    public StyleSheet? StyleSheet { get; set; }
    public UiSize Size { get; private set; }
    public bool IsDirty => _dirty;
    public int LayoutPasses => _layout.LayoutPasses;

    /// <summary>
    /// The backdrop-filter regions collected by the last raster pass, in paint
    /// order (ascending z-index, document order for ties). Read after
    /// <see cref="Render"/>; the WebGPU compositor draws one instanced quad per
    /// region between the 3D scene and the UI overlay.
    /// </summary>
    public IReadOnlyList<BackdropRegion> Backdrops => _backdrops;

    public void Resize(int width, int height)
    {
        Size = new(width, height);
        _surface?.Dispose();
        _surface = null;
        _bitmap?.Dispose();
        _bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        // The canvas draws into the bitmap through a surface so backdrop-filter
        // can snapshot the already-painted backdrop (see DrawPanel).
        _surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul),
            _bitmap.GetPixels(), _bitmap.RowBytes);
        _pixels = new byte[width * height * 4];
        _dirty = true;
    }

    public ReadOnlyMemory<byte> Render(ScreenPanel root)
    {
        if (!_dirty && _pixels.Length != 0) return _pixels;
        if (_bitmap is null || _bitmap.Width != Math.Max(1, (int)Size.Width) || _bitmap.Height != Math.Max(1, (int)Size.Height)) Resize(Math.Max(1, (int)Size.Width), Math.Max(1, (int)Size.Height));
        root.SetViewport(Size.Width, Size.Height);
        _layout.Layout(root, Size.Width / Math.Max(0.01f, root.Scale), Size.Height / Math.Max(0.01f, root.Scale), StyleSheet);
        var canvas = _surface!.Canvas;
        canvas.Clear(SKColors.Transparent);
        _backdrops.Clear();
        DrawPanel(canvas, _surface!, root, 0, 0, root.Opacity);
        _bitmap!.PeekPixels().GetPixelSpan().CopyTo(_pixels);
        _dirty = false;
        return _pixels;
    }

    private void DrawPanel(SKCanvas canvas, SKSurface surface, Panel panel, float ox, float oy, float opacity)
    {
        var rect = new SKRect(panel.Layout.X + ox, panel.Layout.Y + oy, panel.Layout.Right + ox, panel.Layout.Bottom + oy);
        var style = panel.ComputedStyle;
        var alpha = (byte)Math.Clamp(style.Opacity * opacity * 255, 0, 255);

        // transform: paint-only (never affects layout). Transformed panels
        // bypass the backdrop-filter and filter layer paths (the transform is
        // applied around the whole paint).
        if (style.HasTransform)
        {
            // The matrix maps the panel's local box (0,0,w,h) onto its global
            // position, with translate/rotate/scale applied around the resolved
            // transform-origin. Children are drawn inside that local space, so
            // they inherit the parent's transform and move with it.
            var saveCount = canvas.Save();
            // Compose with the current CTM (ancestor transforms) instead of
            // replacing it, so nested transforms chain correctly. Children are
            // offset by the panel's own layout origin: their Layout positions
            // are global, but inside the matrix they must be local to the box.
            var matrix = style.Transform.BuildMatrix(rect.Width, rect.Height, style.TransformOrigin, rect.Left, rect.Top);
            var composed = new SKMatrix();
            SKMatrix.Concat(ref composed, canvas.TotalMatrix, matrix);
            canvas.SetMatrix(composed);
            var localRect = new SKRect(0, 0, rect.Width, rect.Height);
            DrawPanelContent(canvas, surface, panel, localRect, alpha, -panel.Layout.X, -panel.Layout.Y, opacity);
            canvas.RestoreToCount(saveCount);
            return;
        }

        // backdrop-filter: the backdrop itself is filtered by the GPU compositor
        // (Backdrop.wgsl) sampling the WebGPU 3D viewport, not by Skia — this is
        // what keeps the frame rate up: the CPU never sees the scene, and the UI
        // raster only re-runs when the UI changes. The panel's own background
        // and children are painted below/after normally and composite over the
        // filtered backdrop on the GPU. Chains the GPU cannot express (e.g.
        // drop-shadow, or a blur that is not first) fall back to the Skia
        // snapshot path, which filters whatever the UI has painted so far.
        if (!style.BackdropFilter.IsNone)
        {
            if (CssFilterFunctions.IsGpuBackdropExpressible(style.BackdropFilter))
            {
                _backdrops.Add(new BackdropRegion(
                    rect.Left, rect.Top, rect.Width, rect.Height,
                    style.BorderRadius, style.Opacity * opacity, style.BackgroundColor, style.BackdropFilter));
            }
            else if (CssFilterFunctions.BuildImageFilter(style.BackdropFilter) is { } backdropFilter)
            {
                using var backdrop = backdropFilter;
                using var filterPaint = new SKPaint { ImageFilter = backdrop, BlendMode = SKBlendMode.Src };
                var saveCount = canvas.Save();
                canvas.ClipRect(rect);
                using var snapshot = surface.Snapshot();
                canvas.DrawImage(snapshot, 0, 0, filterPaint);
                canvas.RestoreToCount(saveCount);
            }
        }

        // filter: the panel's own rendering (background, text, children and
        // scrollbars) is drawn into an offscreen layer and composited with the
        // filter applied, matching the CSS group semantics. Children are drawn
        // inside the layer, so the filter applies to the whole subtree.
        if (!style.Filter.IsNone && CssFilterFunctions.BuildImageFilter(style.Filter) is { } ownFilter)
        {
            using var own = ownFilter;
            using var filterPaint = new SKPaint { ImageFilter = own };
            canvas.SaveLayer(filterPaint);
            DrawPanelContent(canvas, surface, panel, rect, alpha, ox, oy, opacity);
            canvas.Restore();
        }
        else
        {
            DrawPanelContent(canvas, surface, panel, rect, alpha, ox, oy, opacity);
        }
    }

    private void DrawPanelContent(SKCanvas canvas, SKSurface surface, Panel panel, SKRect rect, byte alpha, float ox, float oy, float opacity)
    {
        // Box shadows follow the CSS painting order: outer shadows below the
        // box's own background, inset shadows above it (and below the border),
        // both following the border-box rounded shape.
        DrawBoxShadows(canvas, panel, rect, alpha, inset: false);
        var background = panel.ComputedStyle.BackgroundColor;
        if (background.A > 0)
        {
            using var paint = new SKPaint { Color = new SKColor(background.R, background.G, background.B, (byte)(background.A * alpha / 255)), IsAntialias = true };
            canvas.DrawRoundRect(rect, panel.ComputedStyle.BorderRadius, panel.ComputedStyle.BorderRadius, paint);
        }
        DrawBoxShadows(canvas, panel, rect, alpha, inset: true);
        // Borders paint above the background and below the content; the widths
        // come from the layout pass (they participate in the box model).
        DrawBorders(canvas, panel, rect, alpha);
        var text = panel.TagName == "text" ? panel.Text : panel is TextInput input ? input.Value : string.Empty;
        // An empty focused input still needs a text pass so its caret can be
        // drawn at the beginning of the field.
        if (!string.IsNullOrEmpty(text) || panel is TextInput { IsFocused: true }) DrawText(canvas, panel, rect, text, alpha);
        // Children are drawn at the scrolled position (content coordinates minus
        // the scroll offset) and clipped to the padding box when the panel clips
        // its content (overflow hidden/scroll/auto/clip). Siblings paint in
        // ascending z-index order (stable, so equal z-index keeps document
        // order): a child with a higher z-index draws on top, mirroring CSS
        // stacking within the panel.
        if (panel.ClipsContent)
        {
            canvas.Save();
            var clip = new SKRect(
                rect.Left + panel.LayoutBorder.Left,
                rect.Top + panel.LayoutBorder.Top,
                rect.Right - panel.LayoutBorder.Right,
                rect.Bottom - panel.LayoutBorder.Bottom);
            canvas.ClipRect(clip);
            DrawChildren(canvas, surface, panel, ox, oy, opacity);
            canvas.Restore();
        }
        else DrawChildren(canvas, surface, panel, ox, oy, opacity);
        // Scrollbars overlay the content edge and stay visible regardless of the
        // scroll position, so they are drawn after restoring the clip.
        DrawScrollBars(canvas, panel, ox, oy);
        // The outline is painted last, on top of the element's own rendering,
        // and outside the border box (it never affects layout).
        DrawOutline(canvas, panel, rect, alpha);
    }

    /// <summary>
    /// Draws the children of a panel in stacking order: ascending z-index with a
    /// stable sort so document order is preserved for equal values. The sort is
    /// skipped entirely when every sibling shares the default z-index (0), so
    /// the common case stays allocation-free.
    /// </summary>
    private void DrawChildren(SKCanvas canvas, SKSurface surface, Panel panel, float ox, float oy, float opacity)
    {
        var children = panel.Children;
        if (children.Count > 1 && children.Any(child => child.ComputedStyle.ZIndex != 0))
        {
            foreach (var child in children.OrderBy(child => child.ComputedStyle.ZIndex))
                DrawPanel(canvas, surface, child, ox - panel.ScrollX, oy - panel.ScrollY, opacity);
        }
        else
        {
            foreach (var child in children) DrawPanel(canvas, surface, child, ox - panel.ScrollX, oy - panel.ScrollY, opacity);
        }
    }

    private static void DrawScrollBars(SKCanvas canvas, Panel panel, float ox, float oy)
    {
        // Scrollbar look is customizable per element through CSS
        // (scrollbar-color/width/radius); the values flow from ComputedStyle.
        var style = panel.ComputedStyle;
        var radius = Math.Min(style.ScrollbarRadius, panel.ScrollbarThickness / 2f);
        var trackColor = new SKColor(style.ScrollbarTrackColor.R, style.ScrollbarTrackColor.G, style.ScrollbarTrackColor.B, style.ScrollbarTrackColor.A);
        var thumbColor = new SKColor(style.ScrollbarThumbColor.R, style.ScrollbarThumbColor.G, style.ScrollbarThumbColor.B, style.ScrollbarThumbColor.A);
        if (ScrollBars.ShouldShowVertical(panel))
        {
            var track = ScrollBars.VerticalTrack(panel);
            using var trackPaint = new SKPaint { Color = trackColor, IsAntialias = true };
            canvas.DrawRoundRect(track.X + ox, track.Y + oy, track.Width, track.Height, radius, radius, trackPaint);
            var thumb = ScrollBars.VerticalThumb(panel);
            using var thumbPaint = new SKPaint { Color = thumbColor, IsAntialias = true };
            canvas.DrawRoundRect(thumb.X + ox, thumb.Y + oy, thumb.Width, thumb.Height, radius, radius, thumbPaint);
        }
        if (ScrollBars.ShouldShowHorizontal(panel))
        {
            var track = ScrollBars.HorizontalTrack(panel);
            using var trackPaint = new SKPaint { Color = trackColor, IsAntialias = true };
            canvas.DrawRoundRect(track.X + ox, track.Y + oy, track.Width, track.Height, radius, radius, trackPaint);
            var thumb = ScrollBars.HorizontalThumb(panel);
            using var thumbPaint = new SKPaint { Color = thumbColor, IsAntialias = true };
            canvas.DrawRoundRect(thumb.X + ox, thumb.Y + oy, thumb.Width, thumb.Height, radius, radius, thumbPaint);
        }
    }

    /// <summary>
    /// Draws the CSS <c>box-shadow</c> list below the box's background. Each
    /// shadow follows the border-box rounded shape shifted by its offset and
    /// inflated by the spread; blur is applied as a Gaussian (sigma = blur / 2).
    /// Inset shadows draw the translated body clipped to the border box, leaving
    /// the inner band along the edges opposite the offset.
    /// </summary>
    private static void DrawBoxShadows(SKCanvas canvas, Panel panel, SKRect rect, byte alpha, bool inset)
    {
        var shadows = panel.ComputedStyle.BoxShadows;
        if (shadows.Length == 0) return;
        var radius = Math.Min(panel.ComputedStyle.BorderRadius, Math.Min(rect.Width, rect.Height) / 2f);
        foreach (var shadow in shadows.Where(shadow => shadow.Inset == inset))
        {
            using var paint = new SKPaint
            {
                Color = new SKColor(shadow.Color.R, shadow.Color.G, shadow.Color.B, (byte)(shadow.Color.A * alpha / 255)),
                IsAntialias = true
            };
            if (shadow.BlurRadius > 0)
                // Decal (not the default Clamp) so the blur never smears edge
                // content across the filter bounds — Clamp mirrors the shadow
                // band to the opposite side of an inset ring.
                paint.ImageFilter = SKImageFilter.CreateBlur(shadow.BlurRadius * 0.5f, shadow.BlurRadius * 0.5f, SKShaderTileMode.Decal, null);

            var spread = shadow.SpreadRadius;
            if (shadow.Inset)
            {
                canvas.Save();
                using (var clipPath = new SKPath())
                {
                    clipPath.AddRoundRect(rect, radius, radius);
                    canvas.ClipPath(clipPath);
                }
                // An inset shadow is the border box minus the shadow shape (the
                // box translated by the offset and contracted by the spread).
                // The shape is clamped to the border box so the even-odd ring
                // stays exactly box \ shape and never fills the shape's
                // out-of-box extension (which would blur a mirrored band on the
                // opposite edge). Positive spread contracts the shape (the
                // corners shrink along with it), matching the CSS spec.
                using (var ring = new SKPath { FillType = SKPathFillType.EvenOdd })
                {
                    ring.AddRoundRect(rect, radius, radius);
                    var hole = new SKRect(
                        Math.Clamp(rect.Left + shadow.OffsetX + spread, rect.Left, rect.Right),
                        Math.Clamp(rect.Top + shadow.OffsetY + spread, rect.Top, rect.Bottom),
                        Math.Clamp(rect.Right + shadow.OffsetX - spread, rect.Left, rect.Right),
                        Math.Clamp(rect.Bottom + shadow.OffsetY - spread, rect.Top, rect.Bottom));
                    var holeRadius = Math.Max(0, radius - spread);
                    ring.AddRoundRect(hole, holeRadius, holeRadius);
                    canvas.DrawPath(ring, paint);
                }
                canvas.Restore();
            }
            else
            {
                var shadowRadius = Math.Max(0, radius + spread);
                var shadowRect = new SKRect(
                    rect.Left + shadow.OffsetX - spread,
                    rect.Top + shadow.OffsetY - spread,
                    rect.Right + shadow.OffsetX + spread,
                    rect.Bottom + shadow.OffsetY + spread);
                using (var path = new SKPath())
                {
                    path.AddRoundRect(shadowRect, shadowRadius, shadowRadius);
                    canvas.DrawPath(path, paint);
                }
            }
        }
    }

    private enum BorderSide { Top, Right, Bottom, Left }

    /// <summary>
    /// Draws the four border sides of a panel inside its border box. The widths
    /// come from the layout pass (<see cref="Panel.LayoutBorder"/>); the style
    /// and color are paint-only and can differ per side. Each side follows the
    /// rounded corners (the corner arcs belong to the horizontal sides, so the
    /// sides join without overlap), matching how browsers carve the border.
    /// </summary>
    private static void DrawBorders(SKCanvas canvas, Panel panel, SKRect rect, byte alpha)
    {
        var style = panel.ComputedStyle;
        var widths = panel.LayoutBorder;
        var radius = Math.Min(style.BorderRadius, Math.Min(rect.Width, rect.Height) / 2f);
        if (widths.Top > 0 && IsVisibleBorderStyle(style.BorderTopStyle))
            DrawBorderSide(canvas, rect, BorderSide.Top, widths.Top, style.BorderTopStyle, style.BorderTopColor, radius, alpha);
        if (widths.Right > 0 && IsVisibleBorderStyle(style.BorderRightStyle))
            DrawBorderSide(canvas, rect, BorderSide.Right, widths.Right, style.BorderRightStyle, style.BorderRightColor, radius, alpha);
        if (widths.Bottom > 0 && IsVisibleBorderStyle(style.BorderBottomStyle))
            DrawBorderSide(canvas, rect, BorderSide.Bottom, widths.Bottom, style.BorderBottomStyle, style.BorderBottomColor, radius, alpha);
        if (widths.Left > 0 && IsVisibleBorderStyle(style.BorderLeftStyle))
            DrawBorderSide(canvas, rect, BorderSide.Left, widths.Left, style.BorderLeftStyle, style.BorderLeftColor, radius, alpha);
    }

    private static bool IsVisibleBorderStyle(string style) => style is not ("none" or "hidden");

    private static void DrawBorderSide(SKCanvas canvas, SKRect rect, BorderSide side, float width,
        string styleName, UiColor color, float radius, byte alpha)
    {
        using var path = BuildBorderSidePath(rect, side, width, radius);
        var baseColor = new SKColor(color.R, color.G, color.B, (byte)(color.A * alpha / 255));

        // double/groove/ridge draw two parallel strokes, one at the outer edge
        // and one at the inner edge of the border band. groove is carved (dark
        // outer, light inner), ridge is the opposite.
        if (styleName is "double" or "groove" or "ridge")
        {
            var (outerColor, innerColor) = styleName switch
            {
                "groove" => (Darken(baseColor), Lighten(baseColor)),
                "ridge" => (Lighten(baseColor), Darken(baseColor)),
                _ => (baseColor, baseColor)
            };
            var (outerDx, outerDy) = PerpendicularOffset(side, width / 3f);
            var (innerDx, innerDy) = PerpendicularOffset(side, -width / 3f);
            DrawStroke(canvas, path, width / 3f, outerColor, outerDx, outerDy);
            DrawStroke(canvas, path, width / 3f, innerColor, innerDx, innerDy);
            return;
        }

        // inset looks pressed (top/left shaded dark, bottom/right light),
        // outset is the raised counterpart.
        var shade = baseColor;
        if (styleName == "inset")
            shade = side is BorderSide.Top or BorderSide.Left ? Darken(baseColor) : Lighten(baseColor);
        else if (styleName == "outset")
            shade = side is BorderSide.Top or BorderSide.Left ? Lighten(baseColor) : Darken(baseColor);

        SKPathEffect? effect = null;
        if (styleName == "dashed") effect = SKPathEffect.CreateDash([3f * width, 3f * width], 0);
        else if (styleName == "dotted") effect = SKPathEffect.CreateDash([0.02f, 2f * width], 0);
        DrawStroke(canvas, path, width, shade, effect: effect, roundCap: styleName == "dotted");
        effect?.Dispose();
    }

    /// <summary>
    /// Builds the centerline path of one border side, inset by half the border
    /// width so the stroke fills the border band exactly. With rounded corners
    /// the corner arcs belong to the top and bottom sides (a quarter circle of
    /// radius r - width/2 around the corner center), and the vertical sides are
    /// straight lines between the arcs, so the four sides join seamlessly.
    /// </summary>
    private static SKPath BuildBorderSidePath(SKRect rect, BorderSide side, float width, float radius)
    {
        var path = new SKPath();
        var half = width / 2f;
        var rounded = radius > half && radius > 0;
        switch (side)
        {
            case BorderSide.Top when rounded:
            {
                var r = radius - half;
                path.MoveTo(rect.Left + half, rect.Top + radius);
                path.ArcTo(CornerOval(rect.Left + radius, rect.Top + radius, r), 180f, 90f, false);
                path.LineTo(rect.Right - radius, rect.Top + half);
                path.ArcTo(CornerOval(rect.Right - radius, rect.Top + radius, r), 270f, 90f, false);
                break;
            }
            case BorderSide.Top:
                path.MoveTo(rect.Left, rect.Top + half);
                path.LineTo(rect.Right, rect.Top + half);
                break;
            case BorderSide.Right when rounded:
                path.MoveTo(rect.Right - half, rect.Top + radius);
                path.LineTo(rect.Right - half, rect.Bottom - radius);
                break;
            case BorderSide.Right:
                path.MoveTo(rect.Right - half, rect.Top);
                path.LineTo(rect.Right - half, rect.Bottom);
                break;
            case BorderSide.Bottom when rounded:
            {
                var r = radius - half;
                path.MoveTo(rect.Right - half, rect.Bottom - radius);
                path.ArcTo(CornerOval(rect.Right - radius, rect.Bottom - radius, r), 0f, 90f, false);
                path.LineTo(rect.Left + radius, rect.Bottom - half);
                path.ArcTo(CornerOval(rect.Left + radius, rect.Bottom - radius, r), 90f, 90f, false);
                break;
            }
            case BorderSide.Bottom:
                path.MoveTo(rect.Right, rect.Bottom - half);
                path.LineTo(rect.Left, rect.Bottom - half);
                break;
            case BorderSide.Left when rounded:
                path.MoveTo(rect.Left + half, rect.Bottom - radius);
                path.LineTo(rect.Left + half, rect.Top + radius);
                break;
            case BorderSide.Left:
                path.MoveTo(rect.Left + half, rect.Bottom);
                path.LineTo(rect.Left + half, rect.Top);
                break;
        }

        return path;
    }

    private static SKRect CornerOval(float centerX, float centerY, float r) => new(centerX - r, centerY - r, centerX + r, centerY + r);

    /// <summary>Translate that moves a path by <paramref name="distance"/> toward the outside of the box.</summary>
    private static (float Dx, float Dy) PerpendicularOffset(BorderSide side, float distance) => side switch
    {
        BorderSide.Top => (0, -distance),
        BorderSide.Right => (distance, 0),
        BorderSide.Bottom => (0, distance),
        _ => (-distance, 0)
    };

    /// <summary>
    /// Draws the outline around the border box, offset outward by
    /// <c>outline-offset</c>. The outline never affects layout and is painted
    /// on top of the element's own rendering.
    /// </summary>
    private static void DrawOutline(SKCanvas canvas, Panel panel, SKRect rect, byte alpha)
    {
        var style = panel.ComputedStyle;
        if (style.OutlineStyle is "none" or "hidden" || style.OutlineWidth <= 0) return;
        var width = style.OutlineWidth;
        var half = width / 2f;
        var color = new SKColor(style.OutlineColor.R, style.OutlineColor.G, style.OutlineColor.B,
            (byte)(style.OutlineColor.A * alpha / 255));
        var outer = new SKRect(
            rect.Left - style.OutlineOffset - half, rect.Top - style.OutlineOffset - half,
            rect.Right + style.OutlineOffset + half, rect.Bottom + style.OutlineOffset + half);
        var corner = Math.Max(0, style.BorderRadius + style.OutlineOffset + half);

        if (style.OutlineStyle is "double" or "groove" or "ridge")
        {
            var inner = new SKRect(
                outer.Left + 5f * width / 6f, outer.Top + 5f * width / 6f,
                outer.Right - 5f * width / 6f, outer.Bottom - 5f * width / 6f);
            var (outerColor, innerColor) = style.OutlineStyle switch
            {
                "groove" => (Darken(color), Lighten(color)),
                "ridge" => (Lighten(color), Darken(color)),
                _ => (color, color)
            };
            using var outerPath = new SKPath();
            outerPath.AddRoundRect(outer, corner, corner);
            using var innerPath = new SKPath();
            innerPath.AddRoundRect(inner, Math.Max(0, corner - 5f * width / 6f), Math.Max(0, corner - 5f * width / 6f));
            DrawStroke(canvas, outerPath, width / 3f, outerColor);
            DrawStroke(canvas, innerPath, width / 3f, innerColor);
            return;
        }

        using var path = new SKPath();
        path.AddRoundRect(outer, corner, corner);
        SKPathEffect? effect = null;
        if (style.OutlineStyle == "dashed") effect = SKPathEffect.CreateDash([3f * width, 3f * width], 0);
        else if (style.OutlineStyle == "dotted") effect = SKPathEffect.CreateDash([0.02f, 2f * width], 0);
        // inset/outset render as a plain solid ring (CSS outlines have no per-side shading).
        DrawStroke(canvas, path, width, color, effect: effect, roundCap: style.OutlineStyle == "dotted");
        effect?.Dispose();
    }

    private static SKColor Darken(SKColor color) => new(
        (byte)(color.Red * 0.55f), (byte)(color.Green * 0.55f), (byte)(color.Blue * 0.55f), color.Alpha);

    private static SKColor Lighten(SKColor color) => new(
        (byte)(color.Red + (255 - color.Red) * 0.45f),
        (byte)(color.Green + (255 - color.Green) * 0.45f),
        (byte)(color.Blue + (255 - color.Blue) * 0.45f),
        color.Alpha);

    private static void DrawStroke(SKCanvas canvas, SKPath path, float strokeWidth, SKColor color,
        float dx = 0, float dy = 0, SKPathEffect? effect = null, bool roundCap = false)
    {
        using var paint = new SKPaint
        {
            Color = color,
            StrokeWidth = strokeWidth,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = roundCap ? SKStrokeCap.Round : SKStrokeCap.Butt,
            PathEffect = effect
        };
        canvas.Save();
        if (dx != 0 || dy != 0) canvas.Translate(dx, dy);
        canvas.DrawPath(path, paint);
        canvas.Restore();
    }

    private static void DrawText(SKCanvas canvas, Panel panel, SKRect rect, string text, byte alpha)
    {
        var style = panel.ComputedStyle;
        var padding = panel.LayoutPadding;
        var left = rect.Left + padding.Left;
        var top = rect.Top + padding.Top;
        var contentWidth = Math.Max(0, rect.Width - padding.Left - padding.Right);
        var contentHeight = Math.Max(0, rect.Height - padding.Top - padding.Bottom);
        var lineHeight = style.LineHeight > 0 ? style.LineHeight : style.FontSize * 1.25f;

        using var paint = new SKPaint { Color = new SKColor(style.Color.R, style.Color.G, style.Color.B, alpha), IsAntialias = true };
        using var font = new SKFont { Size = style.FontSize };
        var metrics = font.Metrics;
        var lines = panel is TextInput ? [text] : WrapText(text, font, contentWidth);
        var blockHeight = lines.Count * lineHeight;
        var y = style.VerticalAlign.Equals("center", StringComparison.OrdinalIgnoreCase)
            ? top + Math.Max(0, (contentHeight - blockHeight) / 2)
            : style.VerticalAlign.Equals("bottom", StringComparison.OrdinalIgnoreCase)
                ? top + Math.Max(0, contentHeight - blockHeight)
                : top;

        float firstLineX = left;

        foreach (var line in lines)
        {
            var measured = font.MeasureText(line);
            var x = style.TextAlign.Equals("center", StringComparison.OrdinalIgnoreCase)
                ? left + Math.Max(0, (contentWidth - measured) / 2)
                : style.TextAlign.Equals("right", StringComparison.OrdinalIgnoreCase)
                    ? left + Math.Max(0, contentWidth - measured)
                    : left;
            firstLineX = x;
            // Skia reçoit une baseline, pas le sommet du texte. La position doit
            // donc tenir compte de la hauteur de la ligne pour que le glyphe soit
            // réellement centré dans un input ou un bouton.
            var baseline = y + lineHeight / 2f - (metrics.Ascent + metrics.Descent) / 2f;
            if (panel is TextInput selectionInput && selectionInput.HasSelection && line == text)
            {
                var selectionStart = Math.Min(selectionInput.SelectionStart, selectionInput.SelectionEnd);
                var selectionEnd = Math.Max(selectionInput.SelectionStart, selectionInput.SelectionEnd);
                var selectionLeft = x + font.MeasureText(text[..selectionStart]);
                var selectionRight = x + font.MeasureText(text[..selectionEnd]);
                using var selectionPaint = new SKPaint { Color = new SKColor(50, 120, 220, alpha), IsAntialias = true };
                canvas.DrawRect(new SKRect(selectionLeft, y, selectionRight, y + lineHeight), selectionPaint);
            }
            // Text shadows paint below the glyphs (first shadow on top), offset
            // from the text position and optionally blurred.
            foreach (var shadow in style.TextShadows)
            {
                using var shadowPaint = new SKPaint
                {
                    Color = new SKColor(shadow.Color.R, shadow.Color.G, shadow.Color.B, (byte)(shadow.Color.A * alpha / 255)),
                    IsAntialias = true
                };
                if (shadow.BlurRadius > 0)
                    shadowPaint.ImageFilter = SKImageFilter.CreateBlur(shadow.BlurRadius * 0.5f, shadow.BlurRadius * 0.5f, SKShaderTileMode.Decal, null);
                canvas.DrawText(line, x + shadow.OffsetX, baseline + shadow.OffsetY, SKTextAlign.Left, font, shadowPaint);
            }
            canvas.DrawText(line, x, baseline, SKTextAlign.Left, font, paint);
            y += lineHeight;
        }

        if (panel is TextInput input && input.IsFocused && input.CaretVisible && lines.Count == 1)
        {
            var caretX = firstLineX + font.MeasureText(input.Value[..Math.Clamp(input.CaretIndex, 0, input.Value.Length)]);
            using var caretPaint = new SKPaint { Color = paint.Color, StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawLine(caretX, top + 3, caretX, top + Math.Max(font.Size + 3, contentHeight - 3), caretPaint);
        }
    }

    private static List<string> WrapText(string text, SKFont font, float width)
    {
        if (width <= 0 || font.MeasureText(text) <= width) return text.Split('\n').ToList();
        var lines = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var current = string.Empty;
            foreach (var word in rawLine.Split(' '))
            {
                var candidate = string.IsNullOrEmpty(current) ? word : current + " " + word;
                if (!string.IsNullOrEmpty(current) && font.MeasureText(candidate) > width)
                {
                    lines.Add(current);
                    current = word;
                }
                else current = candidate;
            }
            if (!string.IsNullOrEmpty(current)) lines.Add(current);
        }
        return lines.Count == 0 ? [string.Empty] : lines;
    }

    public void MarkDirty() => _dirty = true;
    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
