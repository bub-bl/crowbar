namespace Crowbar.UI;

/// <summary>
/// A backdrop-filter region collected during the tree-walk paint. The region
/// itself is <em>not</em> rasterized on the CPU: the GPU compositor
/// (Ui/Backdrop.slang) samples the WebGPU 3D viewport behind the UI, filters it and
/// writes it into this border box, exactly like S&amp;box's ui_backdropfilter
/// shader. The panel's own background and children are painted by the GPU
/// renderer and composite on top.
/// </summary>
public readonly record struct BackdropRegion(
    float X, float Y, float Width, float Height, float Radius, float Alpha, UiColor Tint, CssFilter Filter);

/// <summary>
/// Prepares the panel tree for the GPU tree-walk painter: it owns the viewport
/// size, style sheet, asset caches and dirty tracking, and runs only the
/// style/cascade/inheritance/layout passes (no raster). The actual painting is
/// performed by the engine's <c>UiTreePainter</c> from the laid-out tree, so
/// this class carries no Skia dependency. It also owns the hover tooltip state
/// (text + screen position) that the painter draws last, on top of everything.
/// </summary>
public sealed class UiLayoutEngine : IDisposable
{
    private readonly YogaLayoutEngine _layout = new();
    private bool _dirty = true;

    // Tooltip overlay: the `tooltip` attribute of the hovered panel, drawn as
    // a small dark box next to the cursor on top of everything. Tracked here so
    // the painter can draw it; changing it marks the frame dirty without
    // reflowing the layout.
    private string? _tooltipText;
    private float _tooltipX;
    private float _tooltipY;

    public StyleSheet? StyleSheet { get; set; }

    /// <summary>Cache used to resolve <c>&lt;icon&gt;</c> intrinsic sizes.</summary>
    public SvgIconCache IconCache { get; set; } = SvgIconCache.Shared;

    /// <summary>
    /// The image cache used to resolve <c>&lt;img&gt;</c> sources and
    /// <c>background-image</c> URLs at layout time. Swap it to route image
    /// dimension lookups through a custom asset pipeline (or a test cache).
    /// </summary>
    public UiImageCache ImageCache { get; set; } = UiImageCache.Shared;

    public UiSize Size { get; private set; }

    /// <summary>The tooltip text currently shown (the hovered panel's <c>tooltip</c> attribute), or null.</summary>
    public string? TooltipText => _tooltipText;

    /// <summary>Screen X of the cursor the tooltip is anchored to (valid when <see cref="TooltipText"/> is set).</summary>
    public float TooltipX => _tooltipX;

    /// <summary>Screen Y of the cursor the tooltip is anchored to (valid when <see cref="TooltipText"/> is set).</summary>
    public float TooltipY => _tooltipY;

    public bool IsDirty => _dirty;
    public int LayoutPasses => _layout.LayoutPasses;

    /// <summary>
    /// Sets the hover tooltip. A null text hides it; otherwise it is drawn next
    /// to the cursor at (x, y) in screen pixels. Only actual changes (text or
    /// position) invalidate a repaint.
    /// </summary>
    public void SetTooltip(string? text, float x, float y)
    {
        if (_tooltipText == text && Math.Abs(_tooltipX - x) < 0.5f && Math.Abs(_tooltipY - y) < 0.5f) return;
        _tooltipText = text;
        _tooltipX = x;
        _tooltipY = y;
        _dirty = true;
    }

    public void Resize(int width, int height)
    {
        Size = new(width, height);
        _dirty = true;
    }

    /// <summary>
    /// Runs only the style/cascade/inheritance/layout passes (no raster),
    /// leaving the tree laid out so the GPU tree-walk painter can record it.
    /// Returns false when nothing is dirty (the caller may skip repainting).
    /// </summary>
    public bool PrepareForGpu(ScreenPanel root)
    {
        if (!_dirty && !root.AnyPaintDirty && !root.AnyStyleDirty && !root.AnyInheritedDirty && !root.LayoutDirty)
            return false;

        root.SetViewport(Size.Width, Size.Height);

        var needsLayout = root.LayoutDirty;
        if (root.AnyStyleDirty)
        {
            if (YogaLayoutEngine.ApplyStylesTracked(root, StyleSheet)) needsLayout = true;
        }
        if (root.AnyInheritedDirty && !needsLayout)
        {
            if (root.InheritanceDirtyRoots.Count > 0)
            {
                foreach (var animated in root.InheritanceDirtyRoots)
                {
                    if (animated.Parent is null) continue;
                    if (YogaLayoutEngine.ApplyInheritanceSubtree(animated)) needsLayout = true;
                }
            }
            else if (YogaLayoutEngine.ApplyInheritanceOnly(root)) needsLayout = true;
            root.AnyInheritedDirty = false;
            root.ClearInheritanceDirtyRoots();
        }
        if (needsLayout)
        {
            _layout.Layout(root, Size.Width / Math.Max(0.01f, root.Scale), Size.Height / Math.Max(0.01f, root.Scale), StyleSheet, ImageCache, IconCache);
        }

        _dirty = false;
        root.AnyPaintDirty = false;
        root.AnyStyleDirty = false;
        root.AnyInheritedDirty = false;
        root.ClearInheritanceDirtyRoots();
        root.ClearStyleDirtyRoots();
        root.ClearDirty();
        return true;
    }

    public void MarkDirty() => _dirty = true;

    public void Dispose()
    {
        // No native resources are owned: the asset caches are shared singletons.
    }
}
