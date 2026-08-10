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

    /// <summary>Native pointer to the rasterized pixels (premultiplied sRGB RGBA), valid after <see cref="Render"/>.</summary>
    nint PixelBuffer { get; }
    /// <summary>Byte stride between rows of <see cref="PixelBuffer"/>.</summary>
    int RowBytes { get; }
    /// <summary>Pixel rectangles damaged by the last <see cref="Render"/> (empty when nothing was repainted).</summary>
    IReadOnlyList<UiRectInt> DamageRects { get; }
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

/// <summary>Kind of a GPU-composited decoration region.</summary>
public enum DecorationKind
{
    /// <summary>An outer (non-inset) box-shadow, drawn above the UI texture with the panel's own box clipped out.</summary>
    OuterShadow = 0,

    /// <summary>A uniform solid border ring (equal widths, colors and styles on all four sides).</summary>
    Border = 1
}

/// <summary>
/// A decoration (outer box-shadow or uniform border) that the Skia raster
/// skips and the GPU composites instead, one instanced quad per region drawn
/// above the UI texture. <c>Quad</c> bounds the rasterized quad; <c>Box</c> is
/// the panel's border box (the shadow's clip mask, or the border's outer
/// ring); <c>Shape</c> is the shadow's rounded shape rect (equal to the box
/// for borders). Colors are straight sRGB with the effective opacity baked
/// into the alpha channel.
/// </summary>
public readonly record struct DecorationRegion(
    DecorationKind Kind,
    float QuadX, float QuadY, float QuadWidth, float QuadHeight,
    float BoxX, float BoxY, float BoxWidth, float BoxHeight,
    float ShapeX, float ShapeY, float ShapeWidth, float ShapeHeight,
    float ShapeRadius, float BlurRadius, float SpreadRadius, float BorderWidth,
    UiColor Color);

/// <summary>
/// A solid panel background that the Skia raster skips and the GPU compositor
/// (Fills.wgsl) draws instead, one instanced quad per region below the UI
/// texture. The quad is the panel's border box (rounded); <c>Color</c> is the
/// straight sRGB fill color with the effective opacity baked into the alpha.
/// </summary>
public readonly record struct FillRegion(
    float X, float Y, float Width, float Height, float Radius, UiColor Color);

/// <summary>Per-panel bits describing which paints the GPU composites (see <see cref="SkiaUiRenderer.CollectDecorations"/>).</summary>
internal enum PanelDecorationFlags
{
    None = 0,
    /// <summary>Every outer box-shadow of the panel is rendered by the GPU; Skia skips them.</summary>
    OuterShadow = 1,
    /// <summary>The panel's uniform solid border is rendered by the GPU; Skia skips it.</summary>
    Border = 2,
    /// <summary>The panel's solid background is rendered by the GPU; Skia skips it.</summary>
    Fill = 4
}

public sealed class SkiaUiRenderer : IUiRenderer, IDisposable
{
    private readonly YogaLayoutEngine _layout = new();
    private SKBitmap? _bitmap;
    private SKSurface? _surface;
    private byte[] _pixels = [];
    private bool _dirty = true;
    private bool _forceFull;
    private bool _collectBackdrops = true;
    private bool _partialCull;
    private readonly List<BackdropRegion> _backdrops = [];
    private readonly List<DecorationRegion> _decorations = [];
    private readonly List<FillRegion> _fills = [];
    private readonly List<UiRectInt> _damage = [];

    public StyleSheet? StyleSheet { get; set; }

    /// <summary>
    /// When enabled, outer box-shadows and uniform solid borders that are safe
    /// to composite on the GPU (see <see cref="CollectDecorations"/>) are
    /// skipped by the Skia raster and emitted as <see cref="Decorations"/> for
    /// the GPU compositor instead. The editor enables this; renderer-only tests
    /// keep it off so the raster stays self-contained.
    /// </summary>
    public bool GpuDecorations { get; set; }

    /// <summary>
    /// When enabled, solid panel backgrounds that are safe to composite on the
    /// GPU (see <see cref="CollectDecorations"/>) are skipped by the Skia
    /// raster and emitted as <see cref="Fills"/> for the GPU compositor
    /// instead. The editor enables this; renderer-only tests keep it off so
    /// the raster stays self-contained.
    /// </summary>
    public bool GpuFills { get; set; }

    /// <summary>
    /// The decoration regions collected by the last render (empty when GPU
    /// decorations are disabled). Read after <see cref="Render"/>; the WebGPU
    /// compositor draws one instanced quad per region above the UI texture.
    /// </summary>
    public IReadOnlyList<DecorationRegion> Decorations => _decorations;

    /// <summary>
    /// The fill regions collected by the last render (empty when GPU fills are
    /// disabled). Read after <see cref="Render"/>; the WebGPU compositor draws
    /// one instanced quad per region below the UI texture, in paint order.
    /// </summary>
    public IReadOnlyList<FillRegion> Fills => _fills;
    public UiSize Size { get; private set; }
    public bool IsDirty => _dirty;
    public int LayoutPasses => _layout.LayoutPasses;
    public nint PixelBuffer => _bitmap is null ? 0 : _bitmap.GetPixels();
    public int RowBytes => _bitmap?.RowBytes ?? 0;
    public IReadOnlyList<UiRectInt> DamageRects => _damage;

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
        _forceFull = true;
    }

    /// <summary>
    /// Rasterizes the UI into the backing bitmap. The raster is incremental:
    /// a full layout pass (and full clear) only runs when the layout is dirty;
    /// ordinary paint-only invalidations (hover, caret, scroll) repaint just the
    /// union of the damaged panel rectangles, clipped. Dynamic animations and
    /// transitions intentionally escalate to a full repaint because their
    /// previous paint extent is not available to a safe partial clear. The damaged
    /// rects are exposed through <see cref="DamageRects"/> so the GPU compositor
    /// uploads only those sub-regions of the texture.
    /// </summary>
    public ReadOnlyMemory<byte> Render(ScreenPanel root)
    {
        if (!_dirty && _bitmap is not null && !root.AnyPaintDirty && !root.AnyStyleDirty && !root.AnyInheritedDirty && !root.LayoutDirty)
        {
            _damage.Clear();
            if (!GpuDecorations) _decorations.Clear();
            if (!GpuFills) _fills.Clear();
            return _pixels;
        }
        if (_bitmap is null || _bitmap.Width != Math.Max(1, (int)Size.Width) || _bitmap.Height != Math.Max(1, (int)Size.Height)) Resize(Math.Max(1, (int)Size.Width), Math.Max(1, (int)Size.Height));
        root.SetViewport(Size.Width, Size.Height);

        // Style-only invalidations (classes, inline styles, pseudo-state) re-run
        // the cascade; they escalate to a full layout only when a
        // layout-affecting property actually changed somewhere. Animation ticks
        // that moved an inherited property (color, opacity, text metrics,
        // shadows) run the cheap inheritance-only refresh instead.
        var needsLayout = root.LayoutDirty;
        if (root.AnyStyleDirty)
        {
            if (YogaLayoutEngine.ApplyStylesTracked(root, StyleSheet)) needsLayout = true;
            root.AnyInheritedDirty = false;
        }
        else if (root.AnyInheritedDirty && !needsLayout)
        {
            if (YogaLayoutEngine.ApplyInheritanceOnly(root)) needsLayout = true;
            root.AnyInheritedDirty = false;
        }
        if (needsLayout)
        {
            _layout.Layout(root, Size.Width / Math.Max(0.01f, root.Scale), Size.Height / Math.Max(0.01f, root.Scale), StyleSheet);
        }

        // Collect the damaged regions and the backdrop-filter regions (mirroring
        // DrawPanel's recursion) when we are not doing a full redraw anyway.
        _damage.Clear();
        _backdrops.Clear();
        // GPU decorations and fills are decided from the current computed
        // styles and the paint order, before the raster (the Skia walk reads
        // the per-panel eligibility flags they set).
        if (GpuDecorations || GpuFills) CollectDecorations(root);
        else { _decorations.Clear(); _fills.Clear(); }
        var fullRedraw = needsLayout || _forceFull;
        if (!fullRedraw)
        {
            fullRedraw = CollectDamage(root, inTransform: false, opacity: root.Opacity);
            if (!fullRedraw && _damage.Count == 0)
            {
                _dirty = false;
                root.AnyPaintDirty = false;
                root.AnyStyleDirty = false;
                root.AnyInheritedDirty = false;
                root.ClearDirty();
                return _pixels;
            }
            fullRedraw |= DamageTooLarge();
            if (!fullRedraw) ComputeSubtreePaintBounds(root, inTransform: false);
        }
        if (fullRedraw)
        {
            _damage.Clear();
            _damage.Add(new UiRectInt(0, 0, Math.Max(1, (int)Size.Width), Math.Max(1, (int)Size.Height)));
        }

        var canvas = _surface!.Canvas;
        if (fullRedraw)
        {
            canvas.Clear(SKColors.Transparent);
            _backdrops.Clear();
            _collectBackdrops = true;
            DrawPanel(canvas, _surface!, root, 0, 0, root.Opacity);
        }
        else
        {
            // Redraw the tree clipped to each damaged rect: the previous
            // frame's pixels outside the clip stay, and the tree repaints every
            // pixel inside it (the root background covers the region), so the
            // result is pixel-identical to a full render within the damage.
            // Panels whose painted extent (box + shadows/blur/outline margins)
            // does not touch the damage are skipped, so a neighboring panel's
            // shadow that bleeds into the region is still redrawn correctly.
            _collectBackdrops = false;
            _partialCull = true;
            try
            {
                foreach (var d in _damage)
                {
                    var rect = new SKRect(d.X, d.Y, d.X + d.Width, d.Y + d.Height);
                    canvas.Save();
                    canvas.ClipRect(rect);
                    using (var clearPaint = new SKPaint { BlendMode = SKBlendMode.Src, Color = SKColors.Transparent })
                        canvas.DrawRect(rect, clearPaint);
                    DrawPanel(canvas, _surface!, root, 0, 0, root.Opacity);
                    canvas.Restore();
                }
            }
            finally { _partialCull = false; }
        }

        _bitmap!.PeekPixels().GetPixelSpan().CopyTo(_pixels);
        root.ClearDirty();
        root.AnyPaintDirty = false;
        root.AnyStyleDirty = false;
        root.AnyInheritedDirty = false;
        _forceFull = false;
        _dirty = false;
        return _pixels;
    }

    private void DrawPanel(SKCanvas canvas, SKSurface surface, Panel panel, float ox, float oy, float opacity, bool inTransform = false)
    {
        var rect = new SKRect(panel.Layout.X + ox, panel.Layout.Y + oy, panel.Layout.Right + ox, panel.Layout.Bottom + oy);
        var style = panel.ComputedStyle;
        var transformed = style.HasTransform;
        // On the partial path, skip subtrees that cannot touch the damage: their
        // pixels are unchanged and stay in the backing bitmap. The cull uses the
        // whole-subtree paint bounds (own box + shadows/blurs/outline unioned
        // with every descendant — see ComputeSubtreePaintBounds), because a
        // panel whose own box misses the damage can still have children that
        // overflow it (e.g. absolutely-positioned cards hanging below their
        // container), and culling the parent would erase them. Transformed
        // panels and panels under a transformed ancestor are always drawn
        // (their children live in a local space whose screen bounds are not
        // simply their layout rect, so damage culling in screen space would be
        // wrong).
        if (_partialCull && !inTransform && !transformed && !panel.SubtreeHasTransform &&
            !IntersectsAnyDamage(SubtreeRect(panel, ox, oy), 0)) return;
        var alpha = (byte)Math.Clamp(style.Opacity * opacity * 255, 0, 255);

        // transform: paint-only (never affects layout). Transformed panels
        // bypass the backdrop-filter and filter layer paths (the transform is
        // applied around the whole paint).
        if (transformed)
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
            DrawPanelContent(canvas, surface, panel, localRect, alpha, -panel.Layout.X, -panel.Layout.Y, opacity, inTransform: true);
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
                // On the partial path the backdrop regions are collected during
                // the damage walk (one full-tree pass) instead of per damaged
                // rect, so they are not duplicated.
                if (_collectBackdrops)
                {
                    _backdrops.Add(new BackdropRegion(
                        rect.Left, rect.Top, rect.Width, rect.Height,
                        style.BorderRadius, style.Opacity * opacity, style.BackgroundColor, style.BackdropFilter));
                }
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
            DrawPanelContent(canvas, surface, panel, rect, alpha, ox, oy, opacity, inTransform);
            canvas.Restore();
        }
        else
        {
            DrawPanelContent(canvas, surface, panel, rect, alpha, ox, oy, opacity, inTransform);
        }
    }

    private void DrawPanelContent(SKCanvas canvas, SKSurface surface, Panel panel, SKRect rect, byte alpha, float ox, float oy, float opacity, bool inTransform = false)
    {
        // Box shadows follow the CSS painting order: outer shadows below the
        // box's own background, inset shadows above it (and below the border),
        // both following the border-box rounded shape. Outer shadows delegated
        // to the GPU (see CollectDecorations) are skipped here.
        if (!GpuDecorations || (panel.GpuDecorationFlags & (byte)PanelDecorationFlags.OuterShadow) == 0)
            DrawBoxShadows(canvas, panel, rect, alpha, inset: false);
        var background = panel.ComputedStyle.BackgroundColor;
        // A solid background delegated to the GPU (see CollectFills) is skipped
        // here: the compositor draws it below the UI texture, which is
        // transparent in that area (the delegation only happens when nothing
        // painted earlier could hide under the quad).
        var fillGpu = GpuFills && (panel.GpuDecorationFlags & (byte)PanelDecorationFlags.Fill) != 0;
        if (background.A > 0 && !fillGpu)
        {
            using var paint = new SKPaint { Color = new SKColor(background.R, background.G, background.B, (byte)(background.A * alpha / 255)), IsAntialias = true };
            canvas.DrawRoundRect(rect, panel.ComputedStyle.BorderRadius, panel.ComputedStyle.BorderRadius, paint);
        }
        DrawBoxShadows(canvas, panel, rect, alpha, inset: true);
        // Borders paint above the background and below the content; the widths
        // come from the layout pass (they participate in the box model). A
        // uniform solid border delegated to the GPU is skipped here.
        if (!GpuDecorations || (panel.GpuDecorationFlags & (byte)PanelDecorationFlags.Border) == 0)
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
            DrawChildren(canvas, surface, panel, ox, oy, opacity, inTransform);
            canvas.Restore();
        }
        else DrawChildren(canvas, surface, panel, ox, oy, opacity, inTransform);
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
    private void DrawChildren(SKCanvas canvas, SKSurface surface, Panel panel, float ox, float oy, float opacity, bool inTransform)
    {
        var children = panel.Children;
        if (children.Count > 1 && children.Any(child => child.ComputedStyle.ZIndex != 0))
        {
            foreach (var child in children.OrderBy(child => child.ComputedStyle.ZIndex))
                DrawPanel(canvas, surface, child, ox - panel.ScrollX, oy - panel.ScrollY, opacity, inTransform);
        }
        else
        {
            foreach (var child in children) DrawPanel(canvas, surface, child, ox - panel.ScrollX, oy - panel.ScrollY, opacity, inTransform);
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

    /// <summary>
    /// The actual painted extent of a text panel's glyphs (and text shadows),
    /// mirroring the layout math of <see cref="DrawText"/> (padding, wrap,
    /// alignment, line-height, vertical-align) but using the real font metrics
    /// per line so the result always covers every painted pixel. Used by the
    /// fill accumulator (CollectFills) to block fills over the glyphs instead
    /// of over the whole — possibly far wider — layout box.
    /// </summary>
    private static SKRect TextPaintExtent(Panel panel, ComputedStyle style, SKRect rect)
    {
        var padding = panel.LayoutPadding;
        var left = rect.Left + padding.Left;
        var top = rect.Top + padding.Top;
        var contentWidth = Math.Max(0, rect.Width - padding.Left - padding.Right);
        var contentHeight = Math.Max(0, rect.Height - padding.Top - padding.Bottom);
        var lineHeight = style.LineHeight > 0 ? style.LineHeight : style.FontSize * 1.25f;
        using var font = new SKFont { Size = style.FontSize };
        var text = panel.TagName == "text" ? panel.Text : panel is TextInput input ? input.Value : string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            // A focused empty input still paints its caret at the start of the line.
            return new SKRect(left - 1, top - 1, left + 3, top + Math.Max(style.FontSize, contentHeight) + 1);
        }

        var metrics = font.Metrics;
        var lines = WrapText(text, font, contentWidth);
        var blockHeight = lines.Count * lineHeight;
        var y = style.VerticalAlign.Equals("center", StringComparison.OrdinalIgnoreCase)
            ? top + Math.Max(0, (contentHeight - blockHeight) / 2)
            : style.VerticalAlign.Equals("bottom", StringComparison.OrdinalIgnoreCase)
                ? top + Math.Max(0, contentHeight - blockHeight)
                : top;
        var xMin = float.MaxValue;
        var xMax = float.MinValue;
        var yMin = float.MaxValue;
        var yMax = float.MinValue;
        for (var i = 0; i < lines.Count; i++)
        {
            var measured = font.MeasureText(lines[i]);
            var x = style.TextAlign.Equals("center", StringComparison.OrdinalIgnoreCase)
                ? left + Math.Max(0, (contentWidth - measured) / 2)
                : style.TextAlign.Equals("right", StringComparison.OrdinalIgnoreCase)
                    ? left + Math.Max(0, contentWidth - measured)
                    : left;
            xMin = Math.Min(xMin, x);
            xMax = Math.Max(xMax, x + measured);
            var lineCenter = y + lineHeight / 2f;
            yMin = Math.Min(yMin, lineCenter + metrics.Ascent);
            yMax = Math.Max(yMax, lineCenter + metrics.Descent);
            y += lineHeight;
        }

        // 1px safety margin for glyph antialiasing.
        var extent = new SKRect(xMin - 1, yMin - 1, xMax + 1, yMax + 1);
        foreach (var shadow in style.TextShadows)
        {
            var m = Math.Abs(shadow.OffsetX) + Math.Abs(shadow.OffsetY) + shadow.BlurRadius;
            extent.Inflate(m, m);
        }
        return extent;
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

    public void MarkDirty() { _dirty = true; _forceFull = true; }

    /// <summary>
    /// Walks the tree once to collect the damaged rectangles of every panel with
    /// a pending paint/style invalidation, plus the GPU backdrop-filter regions
    /// (mirroring <see cref="DrawPanel"/>'s recursion). Returns true when a full
    /// redraw is required instead (a dirty panel is dynamic or lives under a
    /// transformed ancestor whose painted position cannot be cheaply bounded).
    /// </summary>
    private bool CollectDamage(Panel panel, bool inTransform, float opacity)
    {
        var full = false;
        var style = panel.ComputedStyle;
        var transformed = style.HasTransform;
        var effectiveTransform = inTransform || transformed;
        // An animation or transition can paint outside both the previous and
        // current layout boxes (transform, shadow, blur, outline, text-shadow).
        // The renderer does not retain the previous composed style here, so a
        // partial clear cannot safely remove the old frame. Prefer a coherent
        // full redraw for dynamic panels; this is correctness-critical for the
        // demo's continuously animated title, subtitle and action buttons.
        var hasDynamicPaint = style.Animations.Any(animation => animation.HasAnimation)
            || style.Transitions.Any(transition => transition.Duration > 0
                && !transition.Property.Equals("none", StringComparison.OrdinalIgnoreCase));

        if (panel.PaintDirty || panel.StyleDirty)
        {
            if (inTransform || hasDynamicPaint)
            {
                full = true;
            }
            else
            {
                var rect = new SKRect(panel.Layout.X, panel.Layout.Y, panel.Layout.Right, panel.Layout.Bottom);
                // A panel whose every paint is delegated to the GPU (fill,
                // uniform border, outer shadows) and that paints no text, inset
                // shadow, outline or scrollbar leaves the texture untouched
                // when invalidated: the compositor re-uploads the changed
                // parameters instead, so there is no region to repaint here.
                var flags = panel.GpuDecorationFlags;
                var hasOuterShadows = false;
                foreach (var shadow in style.BoxShadows) { if (!shadow.Inset) { hasOuterShadows = true; break; } }
                var allGpu = GpuFills && (flags & (byte)PanelDecorationFlags.Fill) != 0
                    && (GpuDecorations && (flags & (byte)PanelDecorationFlags.OuterShadow) != 0 || !hasOuterShadows)
                    && !PanelRepaintsTexture(panel, style,
                        gpuFill: true, gpuBorder: GpuDecorations && (flags & (byte)PanelDecorationFlags.Border) != 0);
                if (!allGpu)
                {
                    // GPU-composited shadows are not painted into the bitmap, so
                    // their extent does not need to be repainted here.
                    var margin = GpuDecorations && (panel.GpuDecorationFlags & (byte)PanelDecorationFlags.OuterShadow) != 0
                        ? PaintExtentMarginWithoutOuterShadows(style)
                        : PaintExtentMargin(style);
                    rect.Inflate(margin, margin);
                    if (transformed) rect = TransformBounds(rect, style);
                    // Scrolled containers: the content is painted shifted by the
                    // scroll offset, so the damage must cover the content extents.
                    if ((panel.ScrollX != 0 || panel.ScrollY != 0) && panel.Children.Count > 0)
                    {
                        var content = new SKRect(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                        foreach (var child in panel.Children)
                        {
                            content.Left = Math.Min(content.Left, child.Layout.X - panel.ScrollX);
                            content.Top = Math.Min(content.Top, child.Layout.Y - panel.ScrollY);
                            content.Right = Math.Max(content.Right, child.Layout.Right - panel.ScrollX);
                            content.Bottom = Math.Max(content.Bottom, child.Layout.Bottom - panel.ScrollY);
                        }
                        if (content.Left <= content.Right && content.Top <= content.Bottom)
                            rect = Union(rect, content);
                    }
                    AddDamage(rect);
                }
            }
        }

        // Backdrop regions mirror DrawPanel: only in the non-transform path.
        if (!transformed && !style.BackdropFilter.IsNone && CssFilterFunctions.IsGpuBackdropExpressible(style.BackdropFilter))
        {
            _backdrops.Add(new BackdropRegion(
                panel.Layout.X, panel.Layout.Y, panel.Layout.Width, panel.Layout.Height,
                style.BorderRadius, style.Opacity * opacity, style.BackgroundColor, style.BackdropFilter));
        }

        foreach (var child in panel.Children)
            if (CollectDamage(child, effectiveTransform, opacity)) full = true;
        return full;
    }

    /// <summary>
    /// Computes each panel's subtree paint bounds in screen space — the panel's
    /// own border box inflated by its paint-extent margin (box-shadows, blurs,
    /// outline) unioned with every descendant's — plus whether the subtree
    /// contains a transform. Runs post-order on the partial path so the
    /// <see cref="DrawPanel"/> cull can decide whether the panel's painted
    /// pixels can possibly touch the damage: without the descendant bounds, a
    /// panel whose own box misses the damage but whose children overflow it
    /// (absolute positioning, oversized content) would be culled and its
    /// children erased on the next paint-only redraw. Transformed subtrees are
    /// always drawn by the cull, so their (layout-space) bounds are never
    /// consulted; the flag is what keeps their ancestors from culling.
    /// </summary>
    private static (UiRect Bounds, bool HasTransform) ComputeSubtreePaintBounds(Panel panel, bool inTransform)
    {
        var style = panel.ComputedStyle;
        var bounds = new SKRect(panel.Layout.X, panel.Layout.Y, panel.Layout.Right, panel.Layout.Bottom);
        var margin = PaintExtentMargin(style);
        if (margin > 0) bounds.Inflate(margin, margin);
        var hasTransform = inTransform || style.HasTransform;
        foreach (var child in panel.Children)
        {
            var (childBounds, childTransform) = ComputeSubtreePaintBounds(child, hasTransform);
            bounds = Union(bounds, new SKRect(childBounds.X, childBounds.Y, childBounds.Right, childBounds.Bottom));
            hasTransform |= childTransform;
        }
        panel.SubtreePaintBounds = new UiRect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        panel.SubtreeHasTransform = hasTransform;
        return (panel.SubtreePaintBounds, hasTransform);
    }

    /// <summary>
    /// Converts a panel's cached <see cref="Panel.SubtreePaintBounds"/> (layout
    /// space) into a screen-space SKRect by applying the accumulated scroll
    /// offset, matching how <see cref="DrawPanel"/> positions the subtree.
    /// </summary>
    private static SKRect SubtreeRect(Panel panel, float ox, float oy) => new(
        panel.SubtreePaintBounds.X + ox, panel.SubtreePaintBounds.Y + oy,
        panel.SubtreePaintBounds.Right + ox, panel.SubtreePaintBounds.Bottom + oy);

    /// <summary>
    /// Decides which outer box-shadows, uniform borders and solid backgrounds
    /// can be rendered by the GPU instead of Skia, and records them in
    /// <see cref="Decorations"/> and <see cref="Fills"/>. The GPU composites
    /// decorations above the flat UI texture and fills below it, so a paint is
    /// eligible only when nothing painted after its panel can cover it (for
    /// decorations) or nothing painted before it has content in the area (for
    /// fills, paint order, z-sorted exactly like the Skia walk), no ancestor
    /// clip cuts it, and no transform/filter group wraps the panel — otherwise
    /// the quad would hide later paint or escape a clip. Eligibility is stored
    /// in <see cref="Panel.GpuDecorationFlags"/> so the Skia walk skips the
    /// delegated paints.
    /// </summary>
    private void CollectDecorations(ScreenPanel root)
    {
        _decorations.Clear();
        _fills.Clear();
        var order = new List<(Panel Panel, SKRect Extent, bool Filtered, bool Transformed, SKRect Clip, float Opacity)>();
        CollectPaintOrder(root, order, inTransform: false, filtered: false,
            clip: new SKRect(0, 0, Math.Max(1, Size.Width), Math.Max(1, Size.Height)),
            opacity: root.Opacity, ox: 0, oy: 0);
        CollectFills(order);

        for (var i = 0; i < order.Count; i++)
        {
            var (panel, rect, filtered, transformed, clip, opacity) = order[i];
            if (filtered || transformed) continue;
            var style = panel.ComputedStyle;
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            var radius = Math.Min(style.BorderRadius, Math.Min(rect.Width, rect.Height) / 2f);

            // Outer box-shadows: eligible only when every outer shadow is safe
            // (a partial delegation would need per-shadow flags).
            var allOuterSafe = true;
            var outerRegions = new List<DecorationRegion>();
            foreach (var shadow in style.BoxShadows)
            {
                if (shadow.Inset) continue;
                var shape = new SKRect(
                    rect.Left + shadow.OffsetX - shadow.SpreadRadius,
                    rect.Top + shadow.OffsetY - shadow.SpreadRadius,
                    rect.Right + shadow.OffsetX + shadow.SpreadRadius,
                    rect.Bottom + shadow.OffsetY + shadow.SpreadRadius);
                var shapeRadius = Math.Max(0, radius + shadow.SpreadRadius);
                var margin = shadow.BlurRadius * 0.5f + 2f;
                var quad = new SKRect(shape.Left - margin, shape.Top - margin, shape.Right + margin, shape.Bottom + margin);
                if (!IsGpuShadowSafe(shape, rect, quad, clip, order, i))
                {
                    allOuterSafe = false;
                    break;
                }

                var alpha = (byte)Math.Clamp(style.Opacity * opacity * 255, 0, 255);
                var color = new UiColor(shadow.Color.R, shadow.Color.G, shadow.Color.B,
                    (byte)(shadow.Color.A * alpha / 255));
                outerRegions.Add(new DecorationRegion(
                    DecorationKind.OuterShadow,
                    quad.Left, quad.Top, quad.Width, quad.Height,
                    rect.Left, rect.Top, rect.Width, rect.Height,
                    shape.Left, shape.Top, shape.Width, shape.Height,
                    shapeRadius, shadow.BlurRadius, shadow.SpreadRadius, 0, color));
            }

            if (allOuterSafe && outerRegions.Count > 0)
            {
                panel.GpuDecorationFlags |= (byte)PanelDecorationFlags.OuterShadow;
                _decorations.AddRange(outerRegions);
            }

            // Uniform solid border: one ring region.
            if (TryGetUniformBorder(panel, style, rect, out var borderWidth, out var borderColor))
            {
                var ringSafe = clip.Contains(rect);
                if (ringSafe)
                {
                    for (var j = i + 1; j < order.Count; j++)
                    {
                        if (RingIntersects(rect, borderWidth, order[j].Extent))
                        {
                            ringSafe = false;
                            break;
                        }
                    }
                }

                if (ringSafe)
                {
                    var alpha = (byte)Math.Clamp(style.Opacity * opacity * 255, 0, 255);
                    var color = new UiColor(borderColor.R, borderColor.G, borderColor.B,
                        (byte)(borderColor.A * alpha / 255));
                    panel.GpuDecorationFlags |= (byte)PanelDecorationFlags.Border;
                    _decorations.Add(new DecorationRegion(
                        DecorationKind.Border,
                        rect.Left, rect.Top, rect.Width, rect.Height,
                        rect.Left, rect.Top, rect.Width, rect.Height,
                        rect.Left, rect.Top, rect.Width, rect.Height,
                        radius, 0, 0, borderWidth, color));
                }
            }
        }
    }

    /// <summary>
    /// Decides which solid panel backgrounds the GPU composites below the UI
    /// texture (Fills.wgsl) instead of Skia, and records them in
    /// <see cref="Fills"/>. The quads are drawn under the flat UI texture, so
    /// a fill is eligible only when the fill area is clean of every paint that
    /// happened before its panel in paint order: the accumulator
    /// (<paramref name="painted"/>) holds the screen-space extent of all
    /// Skia-painted content (non-delegated fills, text, borders, CPU shadows),
    /// and a quad stacking on another delegated quad is fine (they draw in
    /// order). The panel's own later paint (its text, borders, children) never
    /// blocks its own fill. No ancestor clip may cut the quad, and no
    /// transform/filter group or backdrop-filter may wrap the panel — the
    /// backdrop compositor already paints the panel's tint, so delegating the
    /// fill would double-tint. Eligibility is stored in
    /// <see cref="Panel.GpuDecorationFlags"/> so the Skia walk skips the fill.
    /// </summary>
    private void CollectFills(
        List<(Panel Panel, SKRect Extent, bool Filtered, bool Transformed, SKRect Clip, float Opacity)> order)
    {
        var painted = new List<SKRect>();
        foreach (var (panel, rect, filtered, transformed, clip, opacity) in order)
        {
            panel.GpuDecorationFlags = 0;
            var style = panel.ComputedStyle;
            var bg = style.BackgroundColor;
            var fillGpu = false;
            if (GpuFills && bg.A > 0 && !transformed && !filtered && style.BackdropFilter.IsNone
                && rect.Width > 0 && rect.Height > 0 && clip.Contains(rect) && !OverlapsAny(rect, painted))
            {
                fillGpu = true;
                panel.GpuDecorationFlags |= (byte)PanelDecorationFlags.Fill;
                panel.GpuDecorationFlags |= (byte)PanelDecorationFlags.Fill;
                var radius = Math.Min(style.BorderRadius, Math.Min(rect.Width, rect.Height) / 2f);
                var alpha = (byte)Math.Clamp(style.Opacity * opacity * 255, 0, 255);
                _fills.Add(new FillRegion(
                    rect.Left, rect.Top, rect.Width, rect.Height, radius,
                    new UiColor(bg.R, bg.G, bg.B, (byte)(bg.A * alpha / 255))));
            }

            // The texture keeps every paint of this panel that is not delegated
            // to the GPU: the fill itself (when not), the text, a visible
            // border, inset shadows, the outline and the scrollbars — plus the
            // extent of outer shadows and filters, whose blur spreads beyond
            // the box. The border/outer-shadow delegation is decided after
            // this pass, so they are conservatively assumed to stay on the
            // CPU here (that can only block more fills, never fewer).
            var margin = 0f;
            foreach (var shadow in style.BoxShadows)
            {
                if (!shadow.Inset)
                    margin = Math.Max(margin, Math.Abs(shadow.OffsetX) + Math.Abs(shadow.OffsetY) + shadow.BlurRadius + shadow.SpreadRadius);
            }
            margin = Math.Max(margin, FilterExtentMargin(style.Filter));
            margin = Math.Max(margin, style.OutlineWidth + Math.Abs(style.OutlineOffset));
            if (PanelRepaintsTexture(panel, style, fillGpu, gpuBorder: false) || margin > 0)
            {
                var extent = rect;
                // A panel whose only texture paint is its text (no background,
                // border, inset shadow, outline or scrollbars) paints just its
                // glyphs: block fills over the glyph extent, not over the whole
                // (possibly far wider) layout box.
                var text = panel.TagName == "text" ? panel.Text : panel is TextInput input ? input.Value : string.Empty;
                var paintsText = !string.IsNullOrEmpty(text) || panel is TextInput { IsFocused: true };
                var lb = panel.LayoutBorder;
                var textOnly = paintsText && !(bg.A > 0 && !fillGpu)
                    && !((lb.Top > 0 && IsVisibleBorderStyle(style.BorderTopStyle))
                        || (lb.Right > 0 && IsVisibleBorderStyle(style.BorderRightStyle))
                        || (lb.Bottom > 0 && IsVisibleBorderStyle(style.BorderBottomStyle))
                        || (lb.Left > 0 && IsVisibleBorderStyle(style.BorderLeftStyle)))
                    && !style.BoxShadows.Any(shadow => shadow.Inset)
                    && (style.OutlineWidth <= 0 || style.OutlineStyle is "none" or "hidden")
                    && !ScrollBars.ShouldShowVertical(panel) && !ScrollBars.ShouldShowHorizontal(panel);
                if (textOnly) extent = TextPaintExtent(panel, style, rect);
                if (margin > 0) extent.Inflate(margin, margin);
                // Transformed panels paint through their matrix: block fills
                // over their actual (transformed) extent.
                if (transformed) extent = TransformBounds(extent, style);
                painted.Add(extent);
            }
        }
    }

    private static bool OverlapsAny(SKRect rect, List<SKRect> painted)
    {
        foreach (var p in painted)
            if (rect.IntersectsWith(p)) return true;
        return false;
    }

    /// <summary>
    /// True when the panel paints anything into the UI texture that is not
    /// delegated to the GPU: its background (when not GPU-composited), its
    /// text, a visible border (when not GPU-delegated), inset shadows, the
    /// outline or the scrollbars.
    /// </summary>
    private static bool PanelRepaintsTexture(Panel panel, ComputedStyle style, bool gpuFill, bool gpuBorder)
    {
        if (style.BackgroundColor.A > 0 && !gpuFill) return true;
        var text = panel.TagName == "text" ? panel.Text : panel is TextInput input ? input.Value : string.Empty;
        if (!string.IsNullOrEmpty(text) || panel is TextInput { IsFocused: true }) return true;
        var lb = panel.LayoutBorder;
        var hasBorder = (lb.Top > 0 && IsVisibleBorderStyle(style.BorderTopStyle))
            || (lb.Right > 0 && IsVisibleBorderStyle(style.BorderRightStyle))
            || (lb.Bottom > 0 && IsVisibleBorderStyle(style.BorderBottomStyle))
            || (lb.Left > 0 && IsVisibleBorderStyle(style.BorderLeftStyle));
        if (hasBorder && !gpuBorder) return true;
        foreach (var shadow in style.BoxShadows)
            if (shadow.Inset) return true;
        if (style.OutlineWidth > 0 && style.OutlineStyle is not ("none" or "hidden")) return true;
        if (ScrollBars.ShouldShowVertical(panel) || ScrollBars.ShouldShowHorizontal(panel)) return true;
        return false;
    }

    /// <summary>
    /// Walks the tree in paint order (z-index-sorted siblings, mirroring
    /// <see cref="DrawChildren"/>), carrying the effective ancestor state: the
    /// cumulative opacity, whether a transform or filter group wraps the
    /// subtree, and the intersection of ancestor overflow clips.
    /// </summary>
    private static void CollectPaintOrder(Panel panel,
        List<(Panel Panel, SKRect Extent, bool Filtered, bool Transformed, SKRect Clip, float Opacity)> order,
        bool inTransform, bool filtered, SKRect clip, float opacity, float ox, float oy)
    {
        var style = panel.ComputedStyle;
        var effectiveTransform = inTransform || style.HasTransform;
        var effectiveFiltered = filtered || !style.Filter.IsNone;
        order.Add((panel,
            new SKRect(panel.Layout.X + ox, panel.Layout.Y + oy, panel.Layout.Right + ox, panel.Layout.Bottom + oy),
            effectiveFiltered, effectiveTransform, clip, opacity));

        var childClip = clip;
        if (panel.ClipsContent)
        {
            var lb = panel.LayoutBorder;
            var paddingBox = new SKRect(
                panel.Layout.X + lb.Left, panel.Layout.Y + lb.Top,
                panel.Layout.Right - lb.Right, panel.Layout.Bottom - lb.Bottom);
            childClip = SKRect.Intersect(clip, paddingBox);
        }

        // Children paint shifted by this panel's scroll offset (DrawChildren).
        var childOx = ox - panel.ScrollX;
        var childOy = oy - panel.ScrollY;
        var children = panel.Children;
        if (children.Count > 1 && children.Any(child => child.ComputedStyle.ZIndex != 0))
        {
            foreach (var child in children.OrderBy(child => child.ComputedStyle.ZIndex))
                CollectPaintOrder(child, order, effectiveTransform, effectiveFiltered, childClip, opacity, childOx, childOy);
        }
        else
        {
            foreach (var child in children)
                CollectPaintOrder(child, order, effectiveTransform, effectiveFiltered, childClip, opacity, childOx, childOy);
        }
    }

    /// <summary>
    /// A shadow can go to the GPU when its quad is fully inside the ancestor
    /// clips and no panel painted after its owner touches the shadow area (the
    /// shape minus the owner's own box, which the fragment shader clips out).
    /// </summary>
    private static bool IsGpuShadowSafe(SKRect shape, SKRect box, SKRect quad, SKRect clip,
        List<(Panel Panel, SKRect Extent, bool Filtered, bool Transformed, SKRect Clip, float Opacity)> order, int index)
    {
        if (!clip.Contains(quad)) return false;
        for (var j = index + 1; j < order.Count; j++)
        {
            var extent = order[j].Extent;
            if (!shape.IntersectsWith(extent)) continue;
            // Fully inside the owner's box: the shadow is clipped away there.
            if (SKRect.Intersect(box, extent) == extent) continue;
            return false;
        }

        return true;
    }

    /// <summary>True when the rect <paramref name="e"/> touches the border ring of <paramref name="box"/>.</summary>
    private static bool RingIntersects(SKRect box, float width, SKRect e)
    {
        if (!box.IntersectsWith(e)) return false;
        var innerBox = new SKRect(box.Left + width, box.Top + width, box.Right - width, box.Bottom - width);
        return SKRect.Intersect(innerBox, e) != e;
    }

    /// <summary>The border width and color when all four sides are equal, solid and visible (GPU-renderable).</summary>
    private static bool TryGetUniformBorder(Panel panel, ComputedStyle style, SKRect rect, out float width, out UiColor color)
    {
        width = 0;
        color = default;
        var lb = panel.LayoutBorder;
        if (lb.Top <= 0 || lb.Top != lb.Right || lb.Right != lb.Bottom || lb.Bottom != lb.Left) return false;
        if (!IsSolidBorderStyle(style.BorderTopStyle) || !IsSolidBorderStyle(style.BorderRightStyle) ||
            !IsSolidBorderStyle(style.BorderBottomStyle) || !IsSolidBorderStyle(style.BorderLeftStyle)) return false;
        if (style.BorderTopColor != style.BorderRightColor || style.BorderRightColor != style.BorderBottomColor ||
            style.BorderBottomColor != style.BorderLeftColor) return false;
        width = lb.Top;
        color = style.BorderTopColor;
        return true;
    }

    private static bool IsSolidBorderStyle(string style) => style.Equals("solid", StringComparison.OrdinalIgnoreCase);

    private static SKRect Union(SKRect a, SKRect b) => new(
        Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top),
        Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    /// <summary>True when the rect inflated by <paramref name="margin"/> touches any damaged region.</summary>
    private bool IntersectsAnyDamage(SKRect rect, float margin)
    {
        if (margin > 0) rect.Inflate(margin, margin);
        foreach (var d in _damage)
        {
            if (rect.IntersectsWith(new SKRect(d.X, d.Y, d.X + d.Width, d.Y + d.Height))) return true;
        }
        return false;
    }

    private void AddDamage(SKRect rect)
    {
        rect.Intersect(new SKRect(0, 0, Size.Width, Size.Height));
        if (rect.IsEmpty) return;
        var left = (int)MathF.Floor(rect.Left);
        var top = (int)MathF.Floor(rect.Top);
        var right = (int)MathF.Ceiling(rect.Right);
        var bottom = (int)MathF.Ceiling(rect.Bottom);
        _damage.Add(new UiRectInt(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)));
    }

    /// <summary>True when the accumulated damage covers so much of the screen that a full clear + redraw is cheaper.</summary>
    private bool DamageTooLarge()
    {
        if (_damage.Count > 32) return true;
        long area = 0;
        long total = Math.Max(1, (long)Size.Width * (long)Size.Height);
        foreach (var d in _damage) area += (long)d.Width * d.Height;
        return area > total / 2;
    }

    /// <summary>How far a panel's paint can extend beyond its border box (shadows, blurs, outline).</summary>
    private static float PaintExtentMargin(ComputedStyle style) => PaintExtentMargin(style, includeOuterShadows: true);

    /// <summary>Same margin, excluding outer box-shadows (used when the GPU composites them).</summary>
    private static float PaintExtentMarginWithoutOuterShadows(ComputedStyle style) =>
        PaintExtentMargin(style, includeOuterShadows: false);

    private static float PaintExtentMargin(ComputedStyle style, bool includeOuterShadows)
    {
        var margin = 0f;
        foreach (var shadow in style.BoxShadows)
        {
            if (includeOuterShadows || shadow.Inset)
                margin = Math.Max(margin, Math.Abs(shadow.OffsetX) + Math.Abs(shadow.OffsetY) + shadow.BlurRadius + shadow.SpreadRadius);
        }

        foreach (var shadow in style.TextShadows)
            margin = Math.Max(margin, Math.Abs(shadow.OffsetX) + Math.Abs(shadow.OffsetY) + shadow.BlurRadius);
        margin = Math.Max(margin, FilterExtentMargin(style.Filter));
        margin = Math.Max(margin, style.OutlineWidth + Math.Abs(style.OutlineOffset));
        return margin;
    }

    private static float FilterExtentMargin(CssFilter filter)
    {
        var margin = 0f;
        foreach (var function in filter.Functions)
        {
            var name = function.Name.ToLowerInvariant();
            var p = function.Parameters;
            if (name == "blur" && p.Count > 0)
                margin = Math.Max(margin, p[0] * 3f);
            else if (name == "drop-shadow")
                margin = Math.Max(margin,
                    (p.Count > 0 ? Math.Abs(p[0]) : 0) +
                    (p.Count > 1 ? Math.Abs(p[1]) : 0) +
                    (p.Count > 2 ? p[2] * 3f : 0));
        }
        return margin;
    }

    /// <summary>Bounding box of a panel rect mapped through its CSS transform.</summary>
    private static SKRect TransformBounds(SKRect rect, ComputedStyle style)
    {
        var matrix = style.Transform.BuildMatrix(rect.Width, rect.Height, style.TransformOrigin, rect.Left, rect.Top);
        var points = new[]
        {
            new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top),
            new SKPoint(rect.Right, rect.Bottom), new SKPoint(rect.Left, rect.Bottom)
        };
        // MapPoints(SKPoint[]) returns a NEW array in SkiaSharp; the input is not
        // mutated. The bounds of the transformed rect are what damage tracking
        // needs (the partial raster must repaint wherever the transform moved
        // the paint, or the old pixels stay behind as ghost residue).
        var mapped = matrix.MapPoints(points);
        var bounds = new SKRect(mapped[0].X, mapped[0].Y, mapped[0].X, mapped[0].Y);
        for (var i = 1; i < mapped.Length; i++)
        {
            bounds.Left = Math.Min(bounds.Left, mapped[i].X);
            bounds.Top = Math.Min(bounds.Top, mapped[i].Y);
            bounds.Right = Math.Max(bounds.Right, mapped[i].X);
            bounds.Bottom = Math.Max(bounds.Bottom, mapped[i].Y);
        }
        return bounds;
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
