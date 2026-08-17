using System.Numerics;
using System.Runtime.InteropServices;
using Crowbar.Engine.Rendering;
using Crowbar.FileSystems;
using SixLabors.Fonts;
using SixLabors.Fonts.Rendering;

namespace Crowbar.Engine.Rendering2D;

/// <summary>The shape kinds the SDF shader evaluates.</summary>
internal enum ShapeKind : byte
{
    Rect = 0,
    RoundedRect = 1,
    Ellipse = 2,
    Line = 3
}

/// <summary>The two draw kinds produced by the batch builder.</summary>
internal enum BatchKind : byte
{
    /// <summary>Instanced SDF shapes (rects, circles, lines, ...) — one <c>DrawInstanced</c>.</summary>
    Sdf = 0,

    /// <summary>Tessellated triangles (polygons, future paths/text) — one <c>Draw</c>.</summary>
    Triangles = 1,

    /// <summary>Textured image quads sampling the atlas — one <c>Draw</c>.</summary>
    Textured = 2,

    /// <summary>SDF glyph quads sampling the glyph atlas — one <c>Draw</c>.</summary>
    Glyph = 3,

    /// <summary>Instanced soft shadows (box / inner) — one <c>DrawInstanced</c>.</summary>
    Shadow = 4,

    /// <summary>A filtered child layer composited back onto its parent (one <c>Draw</c>).</summary>
    FilterBlit = 5,

    /// <summary>Soft SDF glyph shadows (text drop shadows) — one <c>Draw</c>.</summary>
    GlyphShadow = 6
}

/// <summary>A contiguous run of <see cref="BatchKind"/> draw commands, in paint order.</summary>
internal readonly struct DrawCmd
{
    public readonly BatchKind Kind;
    public readonly int Start;
    public readonly int Count;

    public DrawCmd(BatchKind kind, int start, int count)
    {
        Kind = kind;
        Start = start;
        Count = count;
    }
}

/// <summary>A screen-space clip region (bounding rect + corner radius).</summary>
internal readonly record struct ClipRect(RectF Rect, float Radius);

/// <summary>
/// One instanced SDF shape. The layout mirrors <c>Shaders/Ui/Shape.slang</c>
/// exactly (18 <c>vec4f</c> = 288 bytes); every field is a <see cref="Vector4"/>
/// on a 16-byte boundary so the C# struct maps 1:1 onto the WGSL struct.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SdfInstance
{
    public Vector4 Rect;      // local bounding box: xy = top-left, zw = size
    public Vector4 M0;        // transform row: screen.x = M0.x*lx + M0.y*ly + M0.z
    public Vector4 M1;        // transform row: screen.y = M1.x*lx + M1.y*ly + M1.z
    public Vector4 Color;     // straight sRGB RGBA, used when gradient kind == 0
    public Vector4 Params;    // x = corner radius, y = stroke width (0 = fill), z = border style, w = dash length
    public Vector4 Grad0;     // linear: start (local); radial: center (local); line: start
    public Vector4 Grad1;     // linear: end (local); radial: x = radius, y = focal; line: end
    public Vector4 Stop0;     // gradient stop colors (straight sRGB)
    public Vector4 Stop1;
    public Vector4 Stop2;
    public Vector4 Stop3;
    public Vector4 Offsets;   // gradient stop offsets (0..1, ascending)
    public Vector4 Clip0;     // screen-space clip rects (xy = top-left, zw = size)
    public Vector4 Clip1;
    public Vector4 Clip2;
    public Vector4 Clip3;
    public Vector4 ClipRadii; // clip corner radii (px)
    public Vector4 Flags;     // x = shape kind, y = gradient kind, z = clip count, w = stop count
}

/// <summary>
/// One tessellated triangle vertex (screen-space position + straight sRGB
/// color). Mirrors <c>Shaders/Ui/Polygon.slang</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TriVertex
{
    public Vector2 Position;
    public Vector4 Color;
}

/// <summary>
/// One textured-quad vertex (screen-space position + atlas UV + straight sRGB
/// tint). Mirrors <c>Shaders/Ui/Image.slang</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TexturedVertex
{
    public Vector2 Position;
    public Vector2 Uv;
    public Vector4 Color;
}

/// <summary>
/// One SDF glyph-shadow quad vertex (screen-space position + atlas UV + color
/// + softness). Mirrors <c>Shaders/Ui/GlyphShadow.slang</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GlyphShadowVertex
{
    public Vector2 Position;
    public Vector2 Uv;
    public Vector4 Color;
    public float Softness;
}

/// <summary>
/// Records where a glyph quad was appended so its UVs can be refreshed at
/// frame end. The glyph atlas can grow (repacking every cell) while a frame is
/// being recorded, which orphans the UVs baked into already-emitted quads; the
/// image always holds the cell's current rect, so it re-derives them just
/// before upload.
/// </summary>
internal readonly record struct GlyphPatch(int VertexIndex, Image2D Atlas);

/// <summary>
/// One instanced soft shadow. Mirrors <c>Shaders/Ui/BoxShadow.slang</c> exactly
/// (13 <c>vec4f</c> = 208 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShadowInstance
{
    public Vector4 M0;        // transform row (local -> screen)
    public Vector4 M1;
    public Vector4 Quad;      // rasterization bounds (local): xy top-left, zw size
    public Vector4 Shape;     // shadow shape (local): xy top-left, zw size
    public Vector4 Box;       // panel border box (local): xy top-left, zw size
    public Vector4 Radii;     // x = shape radius, y = box radius, z = blur, w unused
    public Vector4 Color;     // straight sRGB RGBA
    public Vector4 Flags;     // x = kind (0 outer, 1 inner), y = clip count
    public Vector4 Clip0;     // screen-space clip rects (xy top-left, zw size)
    public Vector4 Clip1;
    public Vector4 Clip2;
    public Vector4 Clip3;
    public Vector4 ClipRadii; // clip corner radii
}

/// <summary>
/// The filter pass parameters, mirroring <c>Shaders/Ui/Filter.slang</c>
/// (10 <c>vec4f</c> = 160 bytes): a blur radius plus up to eight ordered ops.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FilterParams
{
    public Vector4 Blur;                    // x = blur radius
    public Vector4 Op0;
    public Vector4 Op1;
    public Vector4 Op2;
    public Vector4 Op3;
    public Vector4 Op4;
    public Vector4 Op5;
    public Vector4 Op6;
    public Vector4 Op7;
    public Vector4 OpCount;                 // x = number of ops
}

/// <summary>A saved parent-layer state while a filtered child layer is being recorded.</summary>
internal sealed class FilterFrame
{
    public List<SdfInstance> Instances = [];
    public List<TriVertex> Triangles = [];
    public List<TexturedVertex> Textured = [];
    public List<ShadowInstance> Shadows = [];
    public List<GlyphShadowVertex> GlyphShadows = [];
    public List<DrawCmd> Commands = [];
    public List<GlyphPatch> GlyphPatches = [];
    public List<GlyphPatch> GlyphShadowPatches = [];
    public ITexture? Target;
    public ITexture? MsaaTarget;
    public ITexture? Depth;
    public Filter2D Filter = Filter2D.None;
}

/// <summary>
/// A rendered child layer produced by <see cref="PushFilter"/>/<see cref="PopFilter"/>
/// (null target/filters are allowed in headless mode).
/// </summary>
internal sealed class FilterLayer
{
    public List<SdfInstance> Instances = [];
    public List<TriVertex> Triangles = [];
    public List<TexturedVertex> Textured = [];
    public List<ShadowInstance> Shadows = [];
    public List<GlyphShadowVertex> GlyphShadows = [];
    public List<DrawCmd> Commands = [];
    public List<GlyphPatch> GlyphPatches = [];
    public List<GlyphPatch> GlyphShadowPatches = [];
    public Filter2D Filter = Filter2D.None;
    public ITexture? Target;
    public ITexture? MsaaTarget;
    public ITexture? Depth;
    public IBuffer? InstanceBuffer;
    public IBuffer? TriangleBuffer;
    public IBuffer? TexturedBuffer;
    public IBuffer? ShadowBuffer;
    public IBuffer? GlyphShadowBuffer;
    public IBindGroup? SdfBindGroup;
    public IBindGroup? ShadowBindGroup;
    public IBindGroup? GlyphShadowBindGroup;
    public IBindGroup? FilterBindGroup;
}

/// <summary>
/// A GPU-first 2D renderer: the drawing surface the UI framework (and the
/// editor's overlays/gizmos/graphs) call instead of Skia. Drawing commands are
/// recorded into reusable, pre-sized buffers and transformed into the smallest
/// possible set of GPU commands — instanced SDF shapes and tessellated
/// triangles — through the backend-neutral <see cref="IGraphicsDevice"/>.
/// WebGPU is never exposed to callers.
///
/// The public surface mirrors a classic immediate canvas
/// (<c>Begin</c>/draw/<c>End</c>) but the backend is entirely GPU-side: no
/// CPU raster, no per-frame allocation once the buffers have reached their
/// steady-state size, and batching that collapses thousands of shapes into a
/// handful of draw calls.
/// </summary>
public sealed class Renderer2D : IDisposable
{
    // Maximum stacked clips captured per shape (fragment shader evaluates each).
    private const int MaxClipsPerShape = 4;
    // Maximum gradient stops baked inline per shape.
    private const int MaxStops = 4;

    private const int SdfInstanceSize = 288; // 18 * 16 bytes
    private const int TriVertexSize = 24;    // vec2 + vec4
    private const int TexturedVertexSize = 32; // vec2 + vec2 + vec4
    private const int ShadowInstanceSize = 208; // 13 * 16 bytes
    private const int FilterParamsSize = 160;   // 10 * 16 bytes
    private const int GlyphShadowVertexSize = 36; // vec2 + vec2 + vec4 + f32

    /// <summary>A cached glyph atlas entry: the packed MSDF cell.</summary>
    private readonly struct GlyphEntry
    {
        public readonly Image2D Atlas;

        public GlyphEntry(Image2D atlas) => Atlas = atlas;
    }

    private readonly IGraphicsDevice? _device;
    private int _width = 1;
    private int _height = 1;
    private bool _recording;
    private bool _disposed;

    // Transform stack: _transforms[top] is the composed local→screen matrix.
    private readonly List<Matrix3x2> _transforms = [];
    private int _transformTop = -1;
    private Matrix3x2 _current = Matrix3x2.Identity;

    // Clip stack: screen-space regions, innermost last.
    private readonly List<ClipRect> _clips = [];

    // Recorded batches. Swapped between the parent and child filter layers, so
    // they cannot be readonly.
    private List<SdfInstance> _instances = [];
    private List<TriVertex> _triangles = [];
    private List<TexturedVertex> _textured = [];
    private List<ShadowInstance> _shadows = [];
    private List<GlyphShadowVertex> _glyphShadows = [];
    private List<DrawCmd> _commands = [];

    // Per-layer records of emitted glyph quads (and shadow quads), so frame end
    // can refresh their UVs after any mid-frame atlas growth/repack.
    private List<GlyphPatch> _glyphPatches = [];
    private List<GlyphPatch> _glyphShadowPatches = [];
    private bool _runOpen;
    private BatchKind _runKind;
    private int _runStart;

    // Filter layer stack: each pushed filter moves the current lists/target
    // into a frame and starts a fresh child layer; popping records a blit.
    private readonly Stack<FilterFrame> _filterStack = new();
    private readonly List<FilterLayer> _filterLayers = [];

    // Reusable scratch for polygon tessellation (no per-frame allocation).
    private readonly List<Vector2> _polyScratch = [];
    private readonly List<Vector2> _polyClipA = [];
    private readonly List<Vector2> _polyClipB = [];
    private readonly List<int> _earIndices = [];
    private readonly List<Vector2> _svgTriangles = [];

    // GPU resources (created lazily; null when headless).
    private const int UISampleCount = 4;
    private ITexture? _target;
    private ITexture? _msaaTarget;
    private ITexture? _depth;
    private IBuffer? _quadBuffer;
    private IBuffer? _instanceBuffer;
    private IBuffer? _triangleBuffer;
    private IBuffer? _texturedBuffer;
    private IBuffer? _viewportBuffer;
    private IPipeline? _sdfPipeline;
    private IPipeline? _trianglePipeline;
    private IPipeline? _texturedPipeline;
    private IBindGroup? _sdfBindGroup;
    private IBindGroup? _triangleBindGroup;
    private IBindGroup? _texturedBindGroup;
    private ITexture? _atlasTextureBound;
    private ISampler? _atlasSampler;
    private TextureAtlas? _atlas;

    // Glyph (text) rendering.
    private readonly Dictionary<(string FontKey, int SizeKey, ushort GlyphId), GlyphEntry> _glyphCache = [];
    private readonly FontManager _fontManager = new();
    private readonly GlyphCollector _glyphCollector;
    private TextureAtlas? _glyphAtlas;

    // Shaped text-run cache. SixLabors shaping/layout/outline-flattening is
    // deterministic per (text, font, size, typography), so a repaint of the
    // same text re-emits the cached glyph quads instead of re-running the
    // shaper (which was the dominant per-frame cost in the profiler). The
    // per-glyph MSDF cells stay cached in _glyphCache; this cache only stores
    // the laid-out quad positions.
    private readonly Dictionary<TextRunKey, ShapedRun> _textRunCache = [];
    private List<ShapedGlyphPlacement>? _runRecording;
    private Vector2 _runOrigin; // text-space origin of the run being recorded
    private const int TextRunCacheCap = 1024;
    private readonly record struct TextRunKey(
        string Text, string FontKey, int SizeKey,
        float MaxWidth, float LineHeight, float LetterSpacing, int Align);
    private sealed class ShapedRun
    {
        public required ShapedGlyphPlacement[] Glyphs;
    }
    private readonly struct ShapedGlyphPlacement
    {
        public readonly ushort GlyphId;
        public readonly float X0, Y0, X1, Y1;
        public ShapedGlyphPlacement(ushort glyphId, float x0, float y0, float x1, float y1)
        {
            GlyphId = glyphId; X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
        }
    }

    // Measure cache for MeasureText: the painter measures every text panel on
    // every repaint (centering, selection, caret), so cache the advance width.
    private readonly Dictionary<(string Text, string FontKey, int SizeKey, float LetterSpacing), float> _measureCache = [];
    private const int MeasureCacheCap = 4096;

    // SVG fill tessellation cache: the transformed + clipped + triangulated
    // triangles of an icon are deterministic per (shape, transform, clip), so
    // a repaint of the same icon re-emits them instead of re-tessellating
    // (EvenOddTriangulator.Triangulate was ~9% of the profile). The color is
    // applied at emit time, so only positions are cached.
    private readonly Dictionary<SvgFillCacheKey, List<Vector2>> _svgFillCache = [];
    private const int SvgFillCacheCap = 512;
    private readonly record struct SvgFillCacheKey(SvgShape Shape, int ElementIndex, Matrix3x2 Transform, RectF Clip, bool Clipped);
    private IPipeline? _glyphPipeline;
    private IBindGroup? _glyphBindGroup;
    private ITexture? _glyphTextureBound;
    private ISampler? _glyphSampler;

    // Shadow + filter passes.
    private IBuffer? _shadowBuffer;
    private IPipeline? _shadowPipeline;
    private IBindGroup? _shadowBindGroup;
    private IPipeline? _filterPipeline;
    private ISampler? _filterSampler;
    private IBuffer? _filterParamsBuffer;
    private IBuffer? _glyphShadowBuffer;
    private IPipeline? _glyphShadowPipeline;
    private IBindGroup? _glyphShadowBindGroup;
    private ITexture? _glyphShadowTextureBound;

    public Renderer2D(IGraphicsDevice? device = null)
    {
        _device = device;
        _glyphCollector = new GlyphCollector(this);
    }

    /// <summary>Whether the renderer is recording between <see cref="Begin"/> and <see cref="End"/>.</summary>
    public bool IsRecording => _recording;

    /// <summary>The offscreen texture the renderer draws into (null until the first <see cref="End"/> with a device).</summary>
    public ITexture? Target => _target;

    /// <summary>Number of SDF shape instances recorded by the current frame (diagnostics/tests).</summary>
    public int InstanceCount => _instances.Count;

    /// <summary>Number of tessellated triangle vertices recorded by the current frame.</summary>
    public int TriangleCount => _triangles.Count;

    /// <summary>Number of textured-quad vertices recorded by the current frame.</summary>
    public int TexturedCount => _textured.Count;

    /// <summary>Number of shadow instances recorded by the current frame.</summary>
    public int ShadowCount => _shadows.Count;

    /// <summary>Number of glyph-shadow vertices recorded by the current frame.</summary>
    public int GlyphShadowCount => _glyphShadows.Count;

    /// <summary>Number of active filter layers (0 = no filter context).</summary>
    public int FilterDepth => _filterStack.Count;

    /// <summary>Number of contiguous draw runs the current frame will emit (diagnostics/tests).</summary>
    public int BatchCount => _commands.Count + (_runOpen ? 1 : 0);

    // Internal access for unit tests and in-process diagnostics.
    internal IReadOnlyList<SdfInstance> Instances => _instances;
    internal IReadOnlyList<TriVertex> Triangles => _triangles;
    internal IReadOnlyList<TexturedVertex> TexturedVerts => _textured;
    internal IReadOnlyList<ShadowInstance> Shadows => _shadows;
    internal IReadOnlyList<GlyphShadowVertex> GlyphShadows => _glyphShadows;
    internal IReadOnlyList<DrawCmd> Commands => _commands;
    internal IReadOnlyList<FilterLayer> FilterLayers => _filterLayers;
    internal int InstanceCapacity => _instances.Capacity;
    internal int TriangleCapacity => _triangles.Capacity;
    internal int TexturedCapacity => _textured.Capacity;
    internal int CommandCapacity => _commands.Capacity;
    internal int GlyphCacheCount => _glyphCache.Count;
    internal Matrix3x2 CurrentTransform => _current;

    // ---------------------------------------------------------------------
    // Frame lifecycle
    // ---------------------------------------------------------------------

    /// <summary>Begins a frame, resetting all recorded geometry (no allocation).</summary>
    public void Begin()
    {
        Begin(_width, _height);
    }

    /// <summary>Begins a frame at the given target size.</summary>
    public void Begin(int width, int height)
    {
        if (_recording)
            throw new InvalidOperationException("Renderer2D.Begin was called while a frame was already being recorded.");
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _recording = true;
        _instances.Clear();
        _triangles.Clear();
        _textured.Clear();
        _shadows.Clear();
        _glyphShadows.Clear();
        _commands.Clear();
        _glyphPatches.Clear();
        _glyphShadowPatches.Clear();
        _transforms.Clear();
        _transformTop = -1;
        _current = Matrix3x2.Identity;
        _clips.Clear();
        _runOpen = false;

        // Release the previous frame's transient filter layers and bind groups.
        foreach (var layer in _filterLayers)
        {
            layer.Target?.Dispose();
            layer.MsaaTarget?.Dispose();
            layer.Depth?.Dispose();
            layer.InstanceBuffer?.Dispose();
            layer.TriangleBuffer?.Dispose();
            layer.TexturedBuffer?.Dispose();
            layer.ShadowBuffer?.Dispose();
            layer.GlyphShadowBuffer?.Dispose();
            layer.SdfBindGroup?.Dispose();
            layer.ShadowBindGroup?.Dispose();
            layer.GlyphShadowBindGroup?.Dispose();
            layer.FilterBindGroup?.Dispose();
        }
        _filterLayers.Clear();
        _filterStack.Clear();
    }

    /// <summary>
    /// Ends the frame: finalizes the batches and, when a device is attached,
    /// uploads the buffers and submits the draw commands to the GPU. Returns
    /// the rendered offscreen texture (null in headless mode).
    /// </summary>
    public ITexture? End()
    {
        if (!_recording)
            return _target;
        _recording = false;
        CloseRun();

        // The glyph atlas can grow (repacking every cell) while a frame is being
        // recorded, orphaning the UVs baked into already-emitted quads. Refresh
        // them from the cached entries' current cell rects before upload.
        PatchGlyphUvs(_textured, _glyphPatches);
        PatchGlyphShadowUvs(_glyphShadows, _glyphShadowPatches);
        foreach (var layer in _filterLayers)
        {
            PatchGlyphUvs(layer.Textured, layer.GlyphPatches);
            PatchGlyphShadowUvs(layer.GlyphShadows, layer.GlyphShadowPatches);
        }

        if (_device is null || _commands.Count == 0)
            return _target;

        EnsureGpuResources();
        Submit();
        return _target;
    }

    // ---------------------------------------------------------------------
    // Geometry
    // ---------------------------------------------------------------------

    public void DrawRect(RectF rect, ColorF color) =>
        EmitShape(ShapeKind.Rect, rect, 0f, 0f, color, GradientData.None);

    public void DrawRoundedRect(RectF rect, float radius, ColorF color) =>
        EmitShape(ShapeKind.RoundedRect, rect, radius, 0f, color, GradientData.None);

    public void DrawCircle(Vector2 center, float radius, ColorF color) =>
        EmitShape(ShapeKind.Ellipse, CircleRect(center, radius), 0f, 0f, color, GradientData.None);

    public void DrawEllipse(RectF bounds, ColorF color) =>
        EmitShape(ShapeKind.Ellipse, bounds, 0f, 0f, color, GradientData.None);

    /// <summary>Draws a stroked rect (a border of <paramref name="strokeWidth"/>).</summary>
    public void DrawRect(RectF rect, float strokeWidth, ColorF color, BorderStyle style = BorderStyle.Solid, float dashLength = 8f) =>
        EmitShape(ShapeKind.Rect, rect, 0f, strokeWidth, color, GradientData.None, style, dashLength);

    /// <summary>
    /// Draws a stroked rounded rect (a border of <paramref name="strokeWidth"/>).
    /// <paramref name="style"/> selects solid, dashed, dotted or double;
    /// <paramref name="dashLength"/> is the on-length (in local px) for
    /// dashed/dotted borders.
    /// </summary>
    public void DrawRoundedRect(RectF rect, float radius, float strokeWidth, ColorF color, BorderStyle style = BorderStyle.Solid, float dashLength = 8f) =>
        EmitShape(ShapeKind.RoundedRect, rect, radius, strokeWidth, color, GradientData.None, style, dashLength);

    /// <summary>Draws a stroked circle (a ring of <paramref name="strokeWidth"/>).</summary>
    public void DrawCircle(Vector2 center, float radius, float strokeWidth, ColorF color, BorderStyle style = BorderStyle.Solid, float dashLength = 8f) =>
        EmitShape(ShapeKind.Ellipse, CircleRect(center, radius), 0f, strokeWidth, color, GradientData.None, style, dashLength);

    /// <summary>Draws a stroked ellipse.</summary>
    public void DrawEllipse(RectF bounds, float strokeWidth, ColorF color, BorderStyle style = BorderStyle.Solid, float dashLength = 8f) =>
        EmitShape(ShapeKind.Ellipse, bounds, 0f, strokeWidth, color, GradientData.None, style, dashLength);

    /// <summary>Draws a line segment with round caps.</summary>
    public void DrawLine(Vector2 a, Vector2 b, float width, ColorF color, BorderStyle style = BorderStyle.Solid, float dashLength = 8f) =>
        EmitLine(a, b, width, color, style, dashLength);

    /// <summary>Draws a polyline as one round-capped line per segment.</summary>
    public void DrawPolyline(ReadOnlySpan<Vector2> points, float width, ColorF color)
    {
        for (var i = 1; i < points.Length; i++)
            EmitLine(points[i - 1], points[i], width, color);
    }

    /// <summary>
    /// Draws a filled polygon. Points are transformed to screen space and
    /// triangulated with an ear-clipping pass; the triangles join the
    /// tessellated batch. Rounded clips are approximated by their bounding
    /// rect for the polygon path (exact in the SDF path).
    /// </summary>
    public void DrawPolygon(ReadOnlySpan<Vector2> points, ColorF color) =>
        EmitPolygon(points, color.ToVector4());

    // ---------------------------------------------------------------------
    // Images
    // ---------------------------------------------------------------------

    /// <summary>
    /// Registers an image with the renderer (uploaded into the shared texture
    /// atlas) and returns a reusable handle. Only valid while recording is not
    /// in progress; registering during a frame is not supported.
    /// </summary>
    public Image2D RegisterImage(Texture2D texture)
    {
        if (_recording)
            throw new InvalidOperationException("Images must be registered outside of Begin/End.");
        _atlas ??= new TextureAtlas(_device);
        return _atlas.Add(texture);
    }

    /// <summary>Decodes an image file (PNG, JPEG, WebP) and registers it (see <see cref="RegisterImage"/>).</summary>
    public Image2D LoadImage(string path) => RegisterImage(Texture2D.Load(path));

    /// <summary>
    /// Draws an image into <paramref name="dest"/> fitted per
    /// <paramref name="fit"/>, tinted by <paramref name="tint"/> (white = no
    /// tint). The quad joins the textured batch, so all images render in one
    /// draw call. <see cref="ImageFit.Cover"/> overflows the rect; clip it
    /// with <see cref="PushClip"/> to crop.
    /// </summary>
    public void DrawImage(RectF dest, Image2D image, ColorF tint, ImageFit fit = ImageFit.Stretch)
    {
        if (image.IsDisposed || tint.A <= 0f || dest.IsEmpty)
            return;

        var fitted = FitRect(dest, image.Width, image.Height, fit);
        var p0 = Vector2.Transform(fitted.TopLeft, _current);
        var p1 = Vector2.Transform(new Vector2(fitted.Right, fitted.Y), _current);
        var p2 = Vector2.Transform(fitted.BottomRight, _current);
        var p3 = Vector2.Transform(new Vector2(fitted.X, fitted.Bottom), _current);
        var uv = image.UvRect;
        var color = tint.ToVector4();
        var u0 = new Vector2(uv.X, uv.Y);
        var u1 = new Vector2(uv.Right, uv.Y);
        var u2 = new Vector2(uv.Right, uv.Bottom);
        var u3 = new Vector2(uv.X, uv.Bottom);

        EnsureRun(BatchKind.Textured);
        _textured.Add(new TexturedVertex { Position = p0, Uv = u0, Color = color });
        _textured.Add(new TexturedVertex { Position = p1, Uv = u1, Color = color });
        _textured.Add(new TexturedVertex { Position = p2, Uv = u2, Color = color });
        _textured.Add(new TexturedVertex { Position = p0, Uv = u0, Color = color });
        _textured.Add(new TexturedVertex { Position = p2, Uv = u2, Color = color });
        _textured.Add(new TexturedVertex { Position = p3, Uv = u3, Color = color });
    }

    private static RectF FitRect(RectF dest, float imageWidth, float imageHeight, ImageFit fit)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
            return dest;
        switch (fit)
        {
            case ImageFit.Stretch:
                return dest;
            case ImageFit.Contain:
            {
                var scale = Math.Min(dest.Width / imageWidth, dest.Height / imageHeight);
                return CenterRect(dest, imageWidth * scale, imageHeight * scale);
            }
            case ImageFit.Cover:
            {
                var scale = Math.Max(dest.Width / imageWidth, dest.Height / imageHeight);
                return CenterRect(dest, imageWidth * scale, imageHeight * scale);
            }
            case ImageFit.Center:
                return CenterRect(dest, imageWidth, imageHeight);
            default:
                return dest;
        }
    }

    private static RectF CenterRect(RectF dest, float width, float height) =>
        new(dest.X + (dest.Width - width) * 0.5f, dest.Y + (dest.Height - height) * 0.5f, width, height);

    // ---------------------------------------------------------------------
    // SVG
    // ---------------------------------------------------------------------

    /// <summary>
    /// Draws a parsed SVG (see <see cref="SvgDocumentParser.Parse"/>) into
    /// <paramref name="dest"/>, scaled uniformly to fit (contain) and centered.
    /// Filled subpaths are even-odd tessellated into the triangle batch; strokes
    /// are emitted as round-capped lines in the SDF batch. <paramref name="tint"/>
    /// resolves every <c>currentColor</c> fill/stroke (literal colors win).
    /// </summary>
    public void DrawSvg(SvgShape svg, RectF dest, ColorF tint)
    {
        if (!_recording || svg is null || dest.IsEmpty || tint.A <= 0f)
            return;

        var view = svg.ViewBox;
        if (view.Width <= 0f || view.Height <= 0f)
            return;

        var scale = MathF.Min(dest.Width / view.Width, dest.Height / view.Height);
        var offset = new Vector2(
            dest.X + (dest.Width - view.Width * scale) * 0.5f - view.X * scale,
            dest.Y + (dest.Height - view.Height * scale) * 0.5f - view.Y * scale);

        PushTranslate(offset);
        PushScale(scale);

        for (var elementIndex = 0; elementIndex < svg.Elements.Count; elementIndex++)
        {
            var element = svg.Elements[elementIndex];
            if (element.Fill.Kind != SvgPaintKind.None)
                EmitSvgFill(svg, elementIndex, element.Contours, ResolvePaint(element.Fill, tint));
            if (element.Stroke.Kind != SvgPaintKind.None)
                EmitSvgStroke(element.Contours, element.StrokeWidth, ResolvePaint(element.Stroke, tint));
        }

        PopTransform();
        PopTransform();
    }

    private static ColorF ResolvePaint(SvgPaint paint, ColorF tint) =>
        paint.Kind == SvgPaintKind.Literal ? paint.Color : tint;

    private void EmitSvgFill(SvgShape shape, int elementIndex, List<SvgContour> contours, ColorF color)
    {
        var clipped = _clips.Count > 0;
        var clip = clipped ? CurrentScreenClip() : default;
        var key = new SvgFillCacheKey(shape, elementIndex, _current, clip, clipped);
        if (_svgFillCache.TryGetValue(key, out var cached))
        {
            var colorVector = color.ToVector4();
            for (var i = 0; i + 2 < cached.Count; i += 3)
                EmitTriangle(cached[i], cached[i + 1], cached[i + 2], colorVector);
            return;
        }

        var screenContours = new List<List<Vector2>>(contours.Count);
        foreach (var contour in contours)
        {
            var count = contour.Points.Count;
            if (count < 3)
                continue;
            var transformed = new List<Vector2>(count);
            foreach (var point in contour.Points)
                transformed.Add(Vector2.Transform(point, _current));

            if (clipped)
            {
                transformed = ClipToRect(transformed, clip);
                if (transformed.Count < 3)
                    continue;
            }
            screenContours.Add(transformed);
        }

        _svgTriangles.Clear();
        EvenOddTriangulator.Triangulate(screenContours, _svgTriangles);

        if (_svgTriangles.Count > 0)
        {
            if (_svgFillCache.Count >= SvgFillCacheCap)
                _svgFillCache.Clear();
            _svgFillCache[key] = [.. _svgTriangles];
        }

        var emittedColor = color.ToVector4();
        for (var i = 0; i + 2 < _svgTriangles.Count; i += 3)
            EmitTriangle(_svgTriangles[i], _svgTriangles[i + 1], _svgTriangles[i + 2], emittedColor);
    }

    private void EmitSvgStroke(List<SvgContour> contours, float strokeWidth, ColorF color)
    {
        foreach (var contour in contours)
        {
            var count = contour.Points.Count;
            if (count < 2)
                continue;
            for (var i = 1; i < count; i++)
                EmitLine(contour.Points[i - 1], contour.Points[i], strokeWidth, color);
            if (contour.Closed && count > 2)
                EmitLine(contour.Points[count - 1], contour.Points[0], strokeWidth, color);
        }
    }

    /// <summary>Clips one polygon against a rect (Sutherland–Hodgman) into a new list.</summary>
    private static List<Vector2> ClipToRect(List<Vector2> polygon, RectF rect)
    {
        var a = new List<Vector2>(polygon.Count + 4);
        var b = new List<Vector2>(polygon.Count + 4);

        a.AddRange(polygon);
        b.Clear();
        ClipEdgeX(a, b, rect.X, keepMin: true);

        a.Clear();
        ClipEdgeY(b, a, rect.Y, keepMin: true);

        b.Clear();
        ClipEdgeX(a, b, rect.Right, keepMin: false);

        a.Clear();
        ClipEdgeY(b, a, rect.Bottom, keepMin: false);

        return a;
    }

    // ---------------------------------------------------------------------
    // Text
    // ---------------------------------------------------------------------

    /// <summary>Draws text at <paramref name="position"/> in the given pixel size and color.</summary>
    public void DrawText(string text, Vector2 position, float fontSize, ColorF color) =>
        DrawText(text, position, new TextStyle(fontSize, color));

    /// <summary>
    /// Draws text with full typography (family, weight, letter spacing,
    /// wrapping, line height, alignment). Glyphs are rasterized into the glyph
    /// atlas on first use (cached per font/size/glyph) and rendered as SDF
    /// quads in a single draw call.
    /// </summary>
    public void DrawText(string text, Vector2 position, in TextStyle style)
    {
        if (!_recording || string.IsNullOrEmpty(text) || style.Color.A <= 0f || style.FontSize <= 0f)
            return;

        var resolved = _fontManager.Resolve(style.Family, style.Weight);
        var sizeKey = (int)MathF.Round(style.FontSize);
        var runKey = new TextRunKey(text, resolved.Key, sizeKey, style.MaxWidth, style.LineHeight, style.LetterSpacing, (int)style.Align);

        // Repaint fast path: the laid-out glyph positions for this exact
        // (text, font, size, typography) were cached the first time it was
        // drawn. Replay the quads — no SixLabors shaping, no outline
        // flattening, no measurement.
        if (_textRunCache.TryGetValue(runKey, out var run))
        {
            ReplayShapedRun(run, position, style, resolved.Key, sizeKey);
            return;
        }

        var options = new TextOptions(resolved.CreateFont(style.FontSize))
        {
            Dpi = 72,
            KerningMode = KerningMode.Standard,
            ColorFontSupport = ColorFontSupport.None
        };
        if (style.MaxWidth > 0f)
            options.WrappingLength = style.MaxWidth;
        if (style.LineHeight > 0f)
            // TextStyle.LineHeight is in pixels (like FontSize); SixLabors'
            // LineSpacing is a line-height multiplier in em.
            options.LineSpacing = style.LineHeight / style.FontSize;
        if (style.LetterSpacing != 0f)
            options.Tracking = style.LetterSpacing / style.FontSize;

        // Horizontal alignment is delegated to SixLabors: TextAlignment centers
        // (or end-aligns) each line inside WrappingLength, measured from Origin.
        // Shifting Origin here as well would double-apply the offset (once on the
        // origin, once on the layout), pushing centered text off to the right.
        if (style.Align != TextAlign.Left)
        {
            options.TextAlignment = style.Align == TextAlign.Center ? TextAlignment.Center : TextAlignment.End;
        }
        options.Origin = position;

        // Record the laid-out quads while shaping (the shadow pass emits the
        // same layout, so record only on the main pass to avoid duplicates).
        // Placements are stored relative to the text origin so a replay at a
        // different position re-positions the whole run.
        _runRecording = [];
        _runOrigin = options.Origin;
        if (style.ShadowColor.A > 0f)
        {
            // Shadow pass first, so every shadow paints under every glyph.
            _glyphCollector.Configure(style.Color, style.ShadowOffset, style.ShadowBlur, style.ShadowColor, resolved.Key, sizeKey, emitMain: false, emitShadow: true);
            TextRenderer.RenderTo(_glyphCollector, text, options);
        }
        _glyphCollector.Configure(style.Color, style.ShadowOffset, style.ShadowBlur, style.ShadowColor, resolved.Key, sizeKey, emitMain: true, emitShadow: false);
        TextRenderer.RenderTo(_glyphCollector, text, options);

        var recorded = _runRecording;
        _runRecording = null;
        if (recorded is { Count: > 0 })
        {
            if (_textRunCache.Count >= TextRunCacheCap)
                _textRunCache.Clear();
            _textRunCache[runKey] = new ShapedRun { Glyphs = recorded.ToArray() };
        }
    }

    /// <summary>
    /// Re-emits the quads of a previously shaped run at <paramref name="position"/>.
    /// The SDF cells are already in the glyph atlas, so this only re-emits the
    /// vertex data — the shadow pass (if any) first, then the main pass, in the
    /// same order the first shape produced.
    /// </summary>
    private void ReplayShapedRun(ShapedRun run, Vector2 position, in TextStyle style, string fontKey, int sizeKey)
    {
        var originX = position.X;
        var originY = position.Y;

        if (style.ShadowColor.A > 0f)
        {
            // Ui/GlyphShadow.slang reconstructs distance in screen pixels, so the
            // blur radius stays in the same unit as TextStyle.ShadowBlur.
            var softness = 0.75f + MathF.Max(style.ShadowBlur, 0f);
            var shadowVector = style.ShadowColor.ToVector4();
            var ox = style.ShadowOffset.X;
            var oy = style.ShadowOffset.Y;
            foreach (var glyph in run.Glyphs)
            {
                if (!_glyphCache.TryGetValue((fontKey, sizeKey, glyph.GlyphId), out var entry))
                    continue;
                var uv = entry.Atlas.UvRect;
                var u0 = new Vector2(uv.X, uv.Y);
                var u1 = new Vector2(uv.Right, uv.Y);
                var u2 = new Vector2(uv.Right, uv.Bottom);
                var u3 = new Vector2(uv.X, uv.Bottom);
                var sx0 = originX + glyph.X0 + ox;
                var sy0 = originY + glyph.Y0 + oy;
                var sx1 = originX + glyph.X1 + ox;
                var sy1 = originY + glyph.Y1 + oy;
                var su0 = u0;
                var su1 = u1;
                var su2 = u2;
                var su3 = u3;
                if (!ClipGlyphQuad(ref sx0, ref sy0, ref sx1, ref sy1, ref su0, ref su1, ref su2, ref su3))
                    continue;
                var shadowIndex = _glyphShadows.Count;
                EnsureRun(BatchKind.GlyphShadow);
                _glyphShadows.Add(new GlyphShadowVertex { Position = new Vector2(sx0, sy0), Uv = su0, Color = shadowVector, Softness = softness });
                _glyphShadows.Add(new GlyphShadowVertex { Position = new Vector2(sx1, sy0), Uv = su1, Color = shadowVector, Softness = softness });
                _glyphShadows.Add(new GlyphShadowVertex { Position = new Vector2(sx1, sy1), Uv = su2, Color = shadowVector, Softness = softness });
                _glyphShadows.Add(new GlyphShadowVertex { Position = new Vector2(sx0, sy0), Uv = su0, Color = shadowVector, Softness = softness });
                _glyphShadows.Add(new GlyphShadowVertex { Position = new Vector2(sx1, sy1), Uv = su2, Color = shadowVector, Softness = softness });
                _glyphShadows.Add(new GlyphShadowVertex { Position = new Vector2(sx0, sy1), Uv = su3, Color = shadowVector, Softness = softness });
                _glyphShadowPatches.Add(new GlyphPatch(shadowIndex, entry.Atlas));
            }
        }

        var colorVector = style.Color.ToVector4();
        foreach (var glyph in run.Glyphs)
        {
            if (!_glyphCache.TryGetValue((fontKey, sizeKey, glyph.GlyphId), out var entry))
                continue;
            var uv = entry.Atlas.UvRect;
            var u0 = new Vector2(uv.X, uv.Y);
            var u1 = new Vector2(uv.Right, uv.Y);
            var u2 = new Vector2(uv.Right, uv.Bottom);
            var u3 = new Vector2(uv.X, uv.Bottom);
            var mx0 = originX + glyph.X0;
            var my0 = originY + glyph.Y0;
            var mx1 = originX + glyph.X1;
            var my1 = originY + glyph.Y1;
            var mu0 = u0;
            var mu1 = u1;
            var mu2 = u2;
            var mu3 = u3;
            if (!ClipGlyphQuad(ref mx0, ref my0, ref mx1, ref my1, ref mu0, ref mu1, ref mu2, ref mu3))
                continue;
            var quadIndex = _textured.Count;
            EnsureRun(BatchKind.Glyph);
            _textured.Add(new TexturedVertex { Position = new Vector2(mx0, my0), Uv = mu0, Color = colorVector });
            _textured.Add(new TexturedVertex { Position = new Vector2(mx1, my0), Uv = mu1, Color = colorVector });
            _textured.Add(new TexturedVertex { Position = new Vector2(mx1, my1), Uv = mu2, Color = colorVector });
            _textured.Add(new TexturedVertex { Position = new Vector2(mx0, my0), Uv = mu0, Color = colorVector });
            _textured.Add(new TexturedVertex { Position = new Vector2(mx1, my1), Uv = mu2, Color = colorVector });
            _textured.Add(new TexturedVertex { Position = new Vector2(mx0, my1), Uv = mu3, Color = colorVector });
            _glyphPatches.Add(new GlyphPatch(quadIndex, entry.Atlas));
        }
    }

    /// <summary>
    /// Called by the glyph collector for each laid-out glyph: rasterizes the
    /// outline into the glyph atlas (cached) and emits its shadow and/or main
    /// SDF quad. The shadow pass runs first (all shadows, then all glyphs).
    /// </summary>
    internal void EmitGlyph(
        IReadOnlyList<Vector2> edges, ColorF color,
        Vector2 shadowOffset, float shadowBlur, ColorF shadowColor,
        string fontKey, int sizeKey, ushort glyphId, bool emitMain, bool emitShadow)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        for (var i = 0; i < edges.Count; i++)
        {
            min = Vector2.Min(min, edges[i]);
            max = Vector2.Max(max, edges[i]);
        }
        var inkWidth = (int)MathF.Ceiling(max.X - min.X);
        var inkHeight = (int)MathF.Ceiling(max.Y - min.Y);
        if (inkWidth <= 0 || inkHeight <= 0)
            return;

        var key = (fontKey, sizeKey, glyphId);
        if (!_glyphCache.TryGetValue(key, out var entry))
        {
            _glyphAtlas ??= new TextureAtlas(_device);
            entry = new GlyphEntry(_glyphAtlas.Add(GlyphRasterizer.Rasterize(edges, min, inkWidth, inkHeight)));
            _glyphCache[key] = entry;
        }

        if (emitMain && _runRecording is not null)
        {
            _runRecording.Add(new ShapedGlyphPlacement(
                glyphId,
                min.X - GlyphRasterizer.Padding - _runOrigin.X,
                min.Y - GlyphRasterizer.Padding - _runOrigin.Y,
                min.X - GlyphRasterizer.Padding + entry.Atlas.Width / (float)GlyphRasterizer.Scale - _runOrigin.X,
                min.Y - GlyphRasterizer.Padding + entry.Atlas.Height / (float)GlyphRasterizer.Scale - _runOrigin.Y));
        }

        var uv = entry.Atlas.UvRect;
        // The atlas cell is rasterized at GlyphRasterizer.Scale×; the quad is
        // drawn at 1× screen size so the SDF is sampled at Scale texels per pixel.
        var x0 = min.X - GlyphRasterizer.Padding;
        var y0 = min.Y - GlyphRasterizer.Padding;
        // The atlas stores the field at Scale× resolution, while the quad is
        // expressed in display pixels. Keep this conversion in floating point
        // so the quad dimensions cannot be truncated before sampling the MSDF.
        var x1 = x0 + entry.Atlas.Width / (float)GlyphRasterizer.Scale;
        var y1 = y0 + entry.Atlas.Height / (float)GlyphRasterizer.Scale;
        var uv0 = new Vector2(uv.X, uv.Y);
        var uv1 = new Vector2(uv.Right, uv.Y);
        var uv2 = new Vector2(uv.Right, uv.Bottom);
        var uv3 = new Vector2(uv.X, uv.Bottom);

        if (emitShadow && shadowColor.A > 0f)
        {
            // Ui/GlyphShadow.slang reconstructs distance in screen pixels: keep
            // the blur radius in the same unit as the style value.
            var softness = 0.75f + MathF.Max(shadowBlur, 0f);
            var shadowVector = shadowColor.ToVector4();
            var sx0 = x0 + shadowOffset.X;
            var sy0 = y0 + shadowOffset.Y;
            var sx1 = x1 + shadowOffset.X;
            var sy1 = y1 + shadowOffset.Y;
            var su0 = uv0;
            var su1 = uv1;
            var su2 = uv2;
            var su3 = uv3;
            if (ClipGlyphQuad(ref sx0, ref sy0, ref sx1, ref sy1, ref su0, ref su1, ref su2, ref su3))
            {
                var shadowIndex = _glyphShadows.Count;
                EnsureRun(BatchKind.GlyphShadow);
                _glyphShadows.Add(new GlyphShadowVertex { Position = new Vector2(sx0, sy0), Uv = su0, Color = shadowVector, Softness = softness });
                _glyphShadows.Add(new GlyphShadowVertex { Position = new Vector2(sx1, sy0), Uv = su1, Color = shadowVector, Softness = softness });
                _glyphShadows.Add(new GlyphShadowVertex { Position = new Vector2(sx1, sy1), Uv = su2, Color = shadowVector, Softness = softness });
                _glyphShadows.Add(new GlyphShadowVertex { Position = new Vector2(sx0, sy0), Uv = su0, Color = shadowVector, Softness = softness });
                _glyphShadows.Add(new GlyphShadowVertex { Position = new Vector2(sx1, sy1), Uv = su2, Color = shadowVector, Softness = softness });
                _glyphShadows.Add(new GlyphShadowVertex { Position = new Vector2(sx0, sy1), Uv = su3, Color = shadowVector, Softness = softness });
                _glyphShadowPatches.Add(new GlyphPatch(shadowIndex, entry.Atlas));
            }
        }

        if (emitMain)
        {
            var colorVector = color.ToVector4();
            var mx0 = x0;
            var my0 = y0;
            var mx1 = x1;
            var my1 = y1;
            var mu0 = uv0;
            var mu1 = uv1;
            var mu2 = uv2;
            var mu3 = uv3;
            if (ClipGlyphQuad(ref mx0, ref my0, ref mx1, ref my1, ref mu0, ref mu1, ref mu2, ref mu3))
            {
                var quadIndex = _textured.Count;
                EnsureRun(BatchKind.Glyph);
                _textured.Add(new TexturedVertex { Position = new Vector2(mx0, my0), Uv = mu0, Color = colorVector });
                _textured.Add(new TexturedVertex { Position = new Vector2(mx1, my0), Uv = mu1, Color = colorVector });
                _textured.Add(new TexturedVertex { Position = new Vector2(mx1, my1), Uv = mu2, Color = colorVector });
                _textured.Add(new TexturedVertex { Position = new Vector2(mx0, my0), Uv = mu0, Color = colorVector });
                _textured.Add(new TexturedVertex { Position = new Vector2(mx1, my1), Uv = mu2, Color = colorVector });
                _textured.Add(new TexturedVertex { Position = new Vector2(mx0, my1), Uv = mu3, Color = colorVector });
                _glyphPatches.Add(new GlyphPatch(quadIndex, entry.Atlas));
            }
        }
    }

    /// <summary>
    /// Refreshes the atlas UVs of every recorded glyph quad from its cached
    /// entry's current cell rect (the atlas may have grown and repacked since
    /// the quad was emitted). Called once at frame end, before upload.
    /// </summary>
    private static void PatchGlyphUvs(List<TexturedVertex> vertices, List<GlyphPatch> patches)
    {
        foreach (var patch in patches)
        {
            var uv = patch.Atlas.UvRect;
            var i = patch.VertexIndex;
            var u0 = new Vector2(uv.X, uv.Y);
            var u1 = new Vector2(uv.Right, uv.Y);
            var u2 = new Vector2(uv.Right, uv.Bottom);
            var u3 = new Vector2(uv.X, uv.Bottom);
            var v = vertices[i];
            v.Uv = u0;
            vertices[i] = v;
            v = vertices[i + 1];
            v.Uv = u1;
            vertices[i + 1] = v;
            v = vertices[i + 2];
            v.Uv = u2;
            vertices[i + 2] = v;
            v = vertices[i + 3];
            v.Uv = u0;
            vertices[i + 3] = v;
            v = vertices[i + 4];
            v.Uv = u2;
            vertices[i + 4] = v;
            v = vertices[i + 5];
            v.Uv = u3;
            vertices[i + 5] = v;
        }
    }

    /// <summary>See <see cref="PatchGlyphUvs"/>; applies to glyph-shadow quads.</summary>
    private static void PatchGlyphShadowUvs(List<GlyphShadowVertex> vertices, List<GlyphPatch> patches)
    {
        foreach (var patch in patches)
        {
            var uv = patch.Atlas.UvRect;
            var i = patch.VertexIndex;
            var u0 = new Vector2(uv.X, uv.Y);
            var u1 = new Vector2(uv.Right, uv.Y);
            var u2 = new Vector2(uv.Right, uv.Bottom);
            var u3 = new Vector2(uv.X, uv.Bottom);
            var v = vertices[i];
            v.Uv = u0;
            vertices[i] = v;
            v = vertices[i + 1];
            v.Uv = u1;
            vertices[i + 1] = v;
            v = vertices[i + 2];
            v.Uv = u2;
            vertices[i + 2] = v;
            v = vertices[i + 3];
            v.Uv = u0;
            vertices[i + 3] = v;
            v = vertices[i + 4];
            v.Uv = u2;
            vertices[i + 4] = v;
            v = vertices[i + 5];
            v.Uv = u3;
            vertices[i + 5] = v;
        }
    }

    /// <summary>
    /// Measures the advance width of a single line of <paramref name="text"/>
    /// using the same font resolution and tracking as <see cref="DrawText"/>,
    /// so selection and caret placement agree with the rendered glyphs.
    /// </summary>
    public float MeasureText(string text, in TextStyle style)
    {
        if (string.IsNullOrEmpty(text))
            return 0f;
        var resolved = _fontManager.Resolve(style.Family, style.Weight);
        var sizeKey = (int)MathF.Round(style.FontSize);
        var key = (text, resolved.Key, sizeKey, style.LetterSpacing);
        if (_measureCache.TryGetValue(key, out var cached))
            return cached;

        var options = new TextOptions(resolved.CreateFont(style.FontSize))
        {
            Dpi = 72,
            KerningMode = KerningMode.Standard,
            ColorFontSupport = ColorFontSupport.None
        };
        if (style.LetterSpacing != 0f)
            options.Tracking = style.LetterSpacing / style.FontSize;
        var width = TextMeasurer.MeasureAdvance(text, options).Width;
        if (_measureCache.Count >= MeasureCacheCap)
            _measureCache.Clear();
        _measureCache[key] = width;
        return width;
    }

    // ---------------------------------------------------------------------
    // Gradients
    // ---------------------------------------------------------------------

    public void DrawRect(RectF rect, in LinearGradient gradient) =>
        EmitShape(ShapeKind.Rect, rect, 0f, 0f, default, GradientData.FromLinear(in gradient));

    public void DrawRoundedRect(RectF rect, float radius, in LinearGradient gradient) =>
        EmitShape(ShapeKind.RoundedRect, rect, radius, 0f, default, GradientData.FromLinear(in gradient));

    public void DrawCircle(Vector2 center, float radius, in LinearGradient gradient) =>
        EmitShape(ShapeKind.Ellipse, CircleRect(center, radius), 0f, 0f, default, GradientData.FromLinear(in gradient));

    public void DrawEllipse(RectF bounds, in LinearGradient gradient) =>
        EmitShape(ShapeKind.Ellipse, bounds, 0f, 0f, default, GradientData.FromLinear(in gradient));

    public void DrawRect(RectF rect, in RadialGradient gradient) =>
        EmitShape(ShapeKind.Rect, rect, 0f, 0f, default, GradientData.FromRadial(in gradient));

    public void DrawRoundedRect(RectF rect, float radius, in RadialGradient gradient) =>
        EmitShape(ShapeKind.RoundedRect, rect, radius, 0f, default, GradientData.FromRadial(in gradient));

    public void DrawCircle(Vector2 center, float radius, in RadialGradient gradient) =>
        EmitShape(ShapeKind.Ellipse, CircleRect(center, radius), 0f, 0f, default, GradientData.FromRadial(in gradient));

    public void DrawEllipse(RectF bounds, in RadialGradient gradient) =>
        EmitShape(ShapeKind.Ellipse, bounds, 0f, 0f, default, GradientData.FromRadial(in gradient));

    // ---------------------------------------------------------------------
    // Clipping
    // ---------------------------------------------------------------------

    /// <summary>Pushes a rectangular (or rounded) clip in the current local space.</summary>
    public void PushClip(RectF rect, float radius = 0f)
    {
        if (!_recording)
            return;
        // Transform the clip to screen space so the fragment shader can apply
        // it independently of each shape's own matrix. Rotation/skew collapse
        // to the axis-aligned bounding box (documented limitation).
        var p0 = Vector2.Transform(rect.TopLeft, _current);
        var p1 = Vector2.Transform(new Vector2(rect.Right, rect.Y), _current);
        var p2 = Vector2.Transform(rect.BottomRight, _current);
        var p3 = Vector2.Transform(new Vector2(rect.X, rect.Bottom), _current);
        var min = Vector2.Min(Vector2.Min(p0, p1), Vector2.Min(p2, p3));
        var max = Vector2.Max(Vector2.Max(p0, p1), Vector2.Max(p2, p3));
        var scale = MathF.Sqrt(MathF.Abs(_current.GetDeterminant()));
        _clips.Add(new ClipRect(RectF.FromLTRB(min.X, min.Y, max.X, max.Y), radius * scale));
    }

    /// <summary>Pops the most recent clip.</summary>
    public void PopClip()
    {
        if (_clips.Count > 0)
            _clips.RemoveAt(_clips.Count - 1);
    }

    // ---------------------------------------------------------------------
    // Transforms
    // ---------------------------------------------------------------------

    /// <summary>
    /// Pushes a transform composed onto the current one. The composition is
    /// pre-multiplied (<c>_current = transform * _current</c>) so the most
    /// recently pushed transform applies to the geometry first, matching the
    /// canvas/Skia convention: <c>translate(10); scale(2)</c> maps a point at
    /// (5, 5) to (10 + 2*5, 2*5) = (20, 10).
    /// </summary>
    public void PushTransform(in Matrix3x2 transform)
    {
        _current = transform * _current;
        _transformTop++;
        if (_transformTop < _transforms.Count)
            _transforms[_transformTop] = _current;
        else
            _transforms.Add(_current);
    }

    public void PushTranslate(Vector2 translation) => PushTransform(Matrix3x2.CreateTranslation(translation));
    public void PushScale(float scale) => PushTransform(Matrix3x2.CreateScale(scale));
    public void PushScale(Vector2 scale) => PushTransform(Matrix3x2.CreateScale(scale));
    public void PushRotate(float radians) => PushTransform(Matrix3x2.CreateRotation(radians));

    /// <summary>Pops the most recent transform.</summary>
    public void PopTransform()
    {
        if (_transformTop < 0)
            return;
        _transformTop--;
        _current = _transformTop >= 0 ? _transforms[_transformTop] : Matrix3x2.Identity;
    }

    // ---------------------------------------------------------------------
    // Shadows
    // ---------------------------------------------------------------------

    /// <summary>
    /// Draws a CSS <c>box-shadow</c>: a blurred rounded rect behind
    /// <paramref name="rect"/>, expanded by <paramref name="spread"/> and shifted
    /// by <paramref name="offset"/>. The panel's own box is cut out, so the
    /// shadow only shows outside it. Draw this before the panel's fill.
    /// </summary>
    public void DrawBoxShadow(RectF rect, float radius, Vector2 offset, float blur, float spread, ColorF color) =>
        EmitShadow(outer: true, rect, radius, offset, blur, spread, color);

    /// <summary>
    /// Draws a CSS inset <c>box-shadow</c>: a blurred shadow inside
    /// <paramref name="rect"/>, shrunk by <paramref name="spread"/> and shifted
    /// by <paramref name="offset"/>. Draw this after the panel's fill.
    /// </summary>
    public void DrawInnerShadow(RectF rect, float radius, Vector2 offset, float blur, float spread, ColorF color) =>
        EmitShadow(outer: false, rect, radius, offset, blur, spread, color);

    private void EmitShadow(bool outer, RectF rect, float radius, Vector2 offset, float blur, float spread, ColorF color)
    {
        if (!_recording || color.A <= 0f)
            return;

        RectF shape;
        float shapeRadius;
        if (outer)
        {
            shape = new RectF(rect.X + offset.X - spread, rect.Y + offset.Y - spread, rect.Width + 2f * spread, rect.Height + 2f * spread);
            shapeRadius = MathF.Max(0f, radius + spread);
        }
        else
        {
            shape = new RectF(rect.X + offset.X + spread, rect.Y + offset.Y + spread, MathF.Max(0f, rect.Width - 2f * spread), MathF.Max(0f, rect.Height - 2f * spread));
            shapeRadius = MathF.Max(0f, radius - spread);
        }
        if (shape.IsEmpty)
            return;

        var instance = new ShadowInstance
        {
            M0 = new Vector4(_current.M11, _current.M21, _current.M31, 0f),
            M1 = new Vector4(_current.M12, _current.M22, _current.M32, 0f),
            Shape = new Vector4(shape.X, shape.Y, shape.Width, shape.Height),
            Box = new Vector4(rect.X, rect.Y, rect.Width, rect.Height),
            Radii = new Vector4(shapeRadius, MathF.Max(0f, radius), MathF.Max(blur, 0f), 0f),
            Color = color.ToVector4(),
            Flags = new Vector4(outer ? 0f : 1f, ClipCount(), 0f, 0f)
        };

        // Rasterization bounds: the shape inflated by half the blur (outer) so
        // the soft falloff is fully covered; the box itself for inner shadows.
        var margin = outer ? blur * 0.5f + 1f : 1f;
        instance.Quad = new Vector4(shape.X - margin, shape.Y - margin, shape.Width + 2f * margin, shape.Height + 2f * margin);

        if (_clips.Count > 0) instance.Clip0 = ClipToVec(_clips[0]);
        if (_clips.Count > 1) instance.Clip1 = ClipToVec(_clips[1]);
        if (_clips.Count > 2) instance.Clip2 = ClipToVec(_clips[2]);
        if (_clips.Count > 3) instance.Clip3 = ClipToVec(_clips[3]);
        instance.ClipRadii = new Vector4(
            _clips.Count > 0 ? _clips[0].Radius : 0f,
            _clips.Count > 1 ? _clips[1].Radius : 0f,
            _clips.Count > 2 ? _clips[2].Radius : 0f,
            _clips.Count > 3 ? _clips[3].Radius : 0f);

        EnsureRun(BatchKind.Shadow);
        _shadows.Add(instance);
    }

    // ---------------------------------------------------------------------
    // Filters
    // ---------------------------------------------------------------------

    /// <summary>
    /// Begins a filtered subtree: drawing between this call and the matching
    /// <see cref="PopFilter"/> is rendered to an offscreen target and composited
    /// back through <paramref name="filter"/> (in declaration order). The
    /// renderer owns the transient target automatically.
    /// </summary>
    public void PushFilter(Filter2D filter)
    {
        if (!_recording)
            return;
        CloseRun();

        _filterStack.Push(new FilterFrame
        {
            Instances = _instances,
            Triangles = _triangles,
            Textured = _textured,
            Shadows = _shadows,
            GlyphShadows = _glyphShadows,
            Commands = _commands,
            GlyphPatches = _glyphPatches,
            GlyphShadowPatches = _glyphShadowPatches,
            Target = _target,
            MsaaTarget = _msaaTarget,
            Depth = _depth,
            Filter = filter
        });

        _instances = [];
        _triangles = [];
        _textured = [];
        _shadows = [];
        _glyphShadows = [];
        _commands = [];
        _glyphPatches = [];
        _glyphShadowPatches = [];
        _runOpen = false;

        if (_device is not null)
        {
            _target = CreateLayerTarget();
            _msaaTarget = CreateLayerMsaaTarget();
            _depth = CreateLayerDepth();
        }
    }

    /// <summary>Ends a filtered subtree and composites it back into the parent layer.</summary>
    public void PopFilter()
    {
        if (!_recording || _filterStack.Count == 0)
            return;
        CloseRun();

        var frame = _filterStack.Pop();
        var child = new FilterLayer
        {
            Instances = _instances,
            Triangles = _triangles,
            Textured = _textured,
            Shadows = _shadows,
            GlyphShadows = _glyphShadows,
            Commands = _commands,
            GlyphPatches = _glyphPatches,
            GlyphShadowPatches = _glyphShadowPatches,
            Filter = frame.Filter,
            Target = _target,
            MsaaTarget = _msaaTarget,
            Depth = _depth
        };

        _instances = frame.Instances;
        _triangles = frame.Triangles;
        _textured = frame.Textured;
        _shadows = frame.Shadows;
        _glyphShadows = frame.GlyphShadows;
        _commands = frame.Commands;
        _glyphPatches = frame.GlyphPatches;
        _glyphShadowPatches = frame.GlyphShadowPatches;
        _target = frame.Target;
        _msaaTarget = frame.MsaaTarget;
        _depth = frame.Depth;

        var blitIndex = _filterLayers.Count;
        _filterLayers.Add(child);
        _commands.Add(new DrawCmd(BatchKind.FilterBlit, blitIndex, 1));
    }

    private ITexture CreateLayerTarget() => _device!.CreateTexture(new TextureDescription
    {
        Width = _width,
        Height = _height,
        Format = TextureFormat.Rgba8Unorm,
        RenderTarget = true,
        Sampled = true,
        CopyDestination = true
    });

    // The multisampled companion receives the layer's rendering; it resolves
    // into the single-sample Target (which is what gets sampled/composited).
    private ITexture CreateLayerMsaaTarget() => _device!.CreateTexture(new TextureDescription
    {
        Width = _width,
        Height = _height,
        Format = TextureFormat.Rgba8Unorm,
        RenderTarget = true,
        SampleCount = UISampleCount
    });

    private ITexture CreateLayerDepth() => _device!.CreateTexture(new TextureDescription
    {
        Width = _width,
        Height = _height,
        Format = TextureFormat.Depth24Plus,
        RenderTarget = true,
        SampleCount = UISampleCount
    });

    /// <summary>Flushes an open run into the command list (no-op when none is open).</summary>
    private void CloseRun()
    {
        if (!_runOpen)
            return;
        _commands.Add(new DrawCmd(_runKind, _runStart, RunLength(_runKind) - _runStart));
        _runOpen = false;
    }

    // ---------------------------------------------------------------------
    // Recording internals
    // ---------------------------------------------------------------------

    private static RectF CircleRect(Vector2 center, float radius) =>
        new(center.X - radius, center.Y - radius, radius * 2f, radius * 2f);

    private void EmitLine(Vector2 a, Vector2 b, float width, ColorF color, BorderStyle style = BorderStyle.Solid, float dashLength = 8f)
    {
        var half = MathF.Max(width, 0f) * 0.5f;
        var min = Vector2.Min(a, b) - new Vector2(half);
        var max = Vector2.Max(a, b) + new Vector2(half);
        var rect = RectF.FromLTRB(min.X, min.Y, max.X, max.Y);
        var instance = BeginInstance(ShapeKind.Line, rect);
        instance.Params = new Vector4(0f, MathF.Max(width, 0f), (float)style, MathF.Max(dashLength, 0f));
        instance.Color = color.ToVector4();
        instance.Grad0 = new Vector4(a.X, a.Y, 0f, 0f);
        instance.Grad1 = new Vector4(b.X, b.Y, 0f, 0f);
        instance.Flags = new Vector4((float)ShapeKind.Line, 0f, ClipCount(), 0f);
        AddInstance(instance);
    }

    private void EmitShape(ShapeKind kind, RectF rect, float radius, float strokeWidth, ColorF color, in GradientData gradient, BorderStyle style = BorderStyle.Solid, float dashLength = 8f)
    {
        if (rect.IsEmpty)
            return;
        var instance = BeginInstance(kind, rect);
        instance.Params = new Vector4(radius, MathF.Max(strokeWidth, 0f), (float)style, MathF.Max(dashLength, 0f));
        instance.Color = color.ToVector4();
        instance.Grad0 = gradient.A;
        instance.Grad1 = gradient.B;
        instance.Stop0 = gradient.Stop0;
        instance.Stop1 = gradient.Stop1;
        instance.Stop2 = gradient.Stop2;
        instance.Stop3 = gradient.Stop3;
        instance.Offsets = gradient.Offsets;
        instance.Flags = new Vector4((float)kind, gradient.Kind, ClipCount(), gradient.StopCount);
        AddInstance(instance);
    }

    /// <summary>Builds an instance with the current transform and clip baked in.</summary>
    private SdfInstance BeginInstance(ShapeKind kind, RectF rect)
    {
        var instance = new SdfInstance
        {
            Rect = new Vector4(rect.X, rect.Y, rect.Width, rect.Height),
            M0 = new Vector4(_current.M11, _current.M21, _current.M31, 0f),
            M1 = new Vector4(_current.M12, _current.M22, _current.M32, 0f),
        };
        var count = Math.Min(MaxClipsPerShape, _clips.Count);
        if (_clips.Count > 0) instance.Clip0 = ClipToVec(_clips[0]);
        if (_clips.Count > 1) instance.Clip1 = ClipToVec(_clips[1]);
        if (_clips.Count > 2) instance.Clip2 = ClipToVec(_clips[2]);
        if (_clips.Count > 3) instance.Clip3 = ClipToVec(_clips[3]);
        instance.ClipRadii = new Vector4(
            _clips.Count > 0 ? _clips[0].Radius : 0f,
            _clips.Count > 1 ? _clips[1].Radius : 0f,
            _clips.Count > 2 ? _clips[2].Radius : 0f,
            _clips.Count > 3 ? _clips[3].Radius : 0f);
        return instance;
    }

    private int ClipCount() => Math.Min(MaxClipsPerShape, _clips.Count);

    private static Vector4 ClipToVec(ClipRect clip) => new(clip.Rect.X, clip.Rect.Y, clip.Rect.Width, clip.Rect.Height);

    private void AddInstance(in SdfInstance instance)
    {
        EnsureRun(BatchKind.Sdf);
        _instances.Add(instance);
    }

    private void EmitPolygon(ReadOnlySpan<Vector2> points, in Vector4 color)
    {
        var count = points.Length;
        if (count < 3)
            return;

        _polyScratch.Clear();
        for (var i = 0; i < count; i++)
            _polyScratch.Add(Vector2.Transform(points[i], _current));

        if (_clips.Count > 0)
        {
            ClipPolygon(CurrentScreenClip());
            if (_polyClipA.Count < 3)
                return;
            Triangulate(_polyClipA, color);
        }
        else
        {
            Triangulate(_polyScratch, color);
        }
    }

    private RectF CurrentScreenClip()
    {
        var result = new RectF(0f, 0f, _width, _height);
        foreach (var clip in _clips)
            result = result.Intersect(clip.Rect);
        return result;
    }

    /// <summary>
    /// Clips an axis-aligned screen-space quad against the clip stack, adjusting
    /// the atlas UVs to the surviving region. Returns false when nothing remains
    /// visible (the caller skips emission). Rounded clip corners are approximated
    /// by the rect, matching the SVG polygon clip path.
    /// </summary>
    private bool ClipGlyphQuad(
        ref float x0, ref float y0, ref float x1, ref float y1,
        ref Vector2 u0, ref Vector2 u1, ref Vector2 u2, ref Vector2 u3)
    {
        if (_clips.Count == 0)
            return true;

        var clip = CurrentScreenClip();
        var nx0 = MathF.Max(x0, clip.X);
        var ny0 = MathF.Max(y0, clip.Y);
        var nx1 = MathF.Min(x1, clip.Right);
        var ny1 = MathF.Min(y1, clip.Bottom);
        if (nx0 >= nx1 || ny0 >= ny1)
            return false;

        var dx = x1 - x0;
        var dy = y1 - y0;
        if (dx <= 0f || dy <= 0f)
            return false;

        // u is linear in x and v is linear in y: the quad maps the atlas rect
        // onto the screen rect with no rotation.
        var uLeft = u0.X;
        var uRight = u1.X;
        var vTop = u0.Y;
        var vBottom = u3.Y;
        var nu0 = uLeft + (nx0 - x0) / dx * (uRight - uLeft);
        var nu1 = uLeft + (nx1 - x0) / dx * (uRight - uLeft);
        var nv0 = vTop + (ny0 - y0) / dy * (vBottom - vTop);
        var nv1 = vTop + (ny1 - y0) / dy * (vBottom - vTop);

        u0 = new Vector2(nu0, nv0);
        u1 = new Vector2(nu1, nv0);
        u2 = new Vector2(nu1, nv1);
        u3 = new Vector2(nu0, nv1);
        x0 = nx0; y0 = ny0; x1 = nx1; y1 = ny1;
        return true;
    }

    /// <summary>
    /// Clips the screen-space polygon in <c>_polyScratch</c> against
    /// <paramref name="rect"/> (Sutherland–Hodgman) into <c>_polyClipA</c>,
    /// ping-ponging through the two reusable buffers.
    /// </summary>
    private void ClipPolygon(RectF rect)
    {
        var a = _polyClipA;
        var b = _polyClipB;

        a.Clear();
        a.AddRange(_polyScratch);

        b.Clear();
        ClipEdgeX(a, b, rect.X, keepMin: true);

        a.Clear();
        ClipEdgeY(b, a, rect.Y, keepMin: true);

        b.Clear();
        ClipEdgeX(a, b, rect.Right, keepMin: false);

        a.Clear();
        ClipEdgeY(b, a, rect.Bottom, keepMin: false);
        // The result lands in _polyClipA after the fourth clip.
    }

    private static void ClipEdgeX(List<Vector2> input, List<Vector2> output, float x, bool keepMin)
    {
        for (var i = 0; i < input.Count; i++)
        {
            var current = input[i];
            var previous = input[(i + input.Count - 1) % input.Count];
            var curIn = keepMin ? current.X >= x : current.X <= x;
            var prevIn = keepMin ? previous.X >= x : previous.X <= x;
            if (curIn)
            {
                if (!prevIn) output.Add(new Vector2(x, LerpY(previous, current, x)));
                output.Add(current);
            }
            else if (prevIn)
            {
                output.Add(new Vector2(x, LerpY(previous, current, x)));
            }
        }
    }

    private static void ClipEdgeY(List<Vector2> input, List<Vector2> output, float y, bool keepMin)
    {
        for (var i = 0; i < input.Count; i++)
        {
            var current = input[i];
            var previous = input[(i + input.Count - 1) % input.Count];
            var curIn = keepMin ? current.Y >= y : current.Y <= y;
            var prevIn = keepMin ? previous.Y >= y : previous.Y <= y;
            if (curIn)
            {
                if (!prevIn) output.Add(new Vector2(LerpX(previous, current, y), y));
                output.Add(current);
            }
            else if (prevIn)
            {
                output.Add(new Vector2(LerpX(previous, current, y), y));
            }
        }
    }

    private static float LerpY(Vector2 a, Vector2 b, float x)
    {
        var t = (x - a.X) / (b.X - a.X);
        return a.Y + (b.Y - a.Y) * t;
    }

    private static float LerpX(Vector2 a, Vector2 b, float y)
    {
        var t = (y - a.Y) / (b.Y - a.Y);
        return a.X + (b.X - a.X) * t;
    }

    /// <summary>Ear-clips a simple polygon (any winding) into triangles.</summary>
    private void Triangulate(List<Vector2> polygon, in Vector4 color)
    {
        var count = polygon.Count;
        if (count < 3)
            return;

        _earIndices.Clear();
        for (var i = 0; i < count; i++)
            _earIndices.Add(i);
        if (SignedArea(polygon) < 0f)
        {
            for (var i = 0; i < count; i++)
                _earIndices[i] = count - 1 - i;
        }

        var guard = 0;
        var maxIterations = count * count + 8;
        while (_earIndices.Count > 3 && guard++ < maxIterations)
        {
            var clipped = false;
            for (var i = 0; i < _earIndices.Count; i++)
            {
                var i0 = _earIndices[(i + _earIndices.Count - 1) % _earIndices.Count];
                var i1 = _earIndices[i];
                var i2 = _earIndices[(i + 1) % _earIndices.Count];
                var a = polygon[i0];
                var b = polygon[i1];
                var c = polygon[i2];
                if (!IsEar(polygon, i0, i1, i2, a, b, c))
                    continue;
                EmitTriangle(a, b, c, color);
                _earIndices.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped)
                break; // Degenerate polygon; avoid an infinite loop.
        }

        if (_earIndices.Count == 3)
            EmitTriangle(polygon[_earIndices[0]], polygon[_earIndices[1]], polygon[_earIndices[2]], color);
    }

    private bool IsEar(List<Vector2> polygon, int i0, int i1, int i2, Vector2 a, Vector2 b, Vector2 c)
    {
        if (Cross(b - a, c - b) <= 0f)
            return false; // Reflex vertex.
        for (var k = 0; k < polygon.Count; k++)
        {
            if (k == i0 || k == i1 || k == i2)
                continue;
            if (PointInTriangle(polygon[k], a, b, c))
                return false;
        }
        return true;
    }

    private static float SignedArea(List<Vector2> polygon)
    {
        var area = 0f;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area * 0.5f;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var d1 = Cross(b - a, p - a);
        var d2 = Cross(c - b, p - b);
        var d3 = Cross(a - c, p - c);
        var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    private void EmitTriangle(Vector2 a, Vector2 b, Vector2 c, in Vector4 color)
    {
        EnsureRun(BatchKind.Triangles);
        _triangles.Add(new TriVertex { Position = a, Color = color });
        _triangles.Add(new TriVertex { Position = b, Color = color });
        _triangles.Add(new TriVertex { Position = c, Color = color });
    }

    /// <summary>Closes the current run and opens a new one when the batch kind changes.</summary>
    private void EnsureRun(BatchKind kind)
    {
        if (_runOpen && _runKind == kind)
            return;
        if (_runOpen)
            _commands.Add(new DrawCmd(_runKind, _runStart, RunLength(_runKind) - _runStart));
        _runOpen = true;
        _runKind = kind;
        _runStart = RunLength(kind);
    }

    private int RunLength(BatchKind kind) => kind switch
    {
        BatchKind.Sdf => _instances.Count,
        BatchKind.Triangles => _triangles.Count,
        BatchKind.Shadow => _shadows.Count,
        BatchKind.GlyphShadow => _glyphShadows.Count,
        _ => _textured.Count
    };

    // ---------------------------------------------------------------------
    // GPU backend (internal; WebGPU never leaks to callers)
    // ---------------------------------------------------------------------

    private void EnsureGpuResources()
    {
        if (_device is null)
            return;

        if (_target is null || _target.Width != _width || _target.Height != _height)
        {
            _target?.Dispose();
            _msaaTarget?.Dispose();
            _depth?.Dispose();
            _target = _device.CreateTexture(new TextureDescription
            {
                Width = _width,
                Height = _height,
                Format = TextureFormat.Rgba8Unorm,
                RenderTarget = true,
                Sampled = true,
                CopyDestination = true
            });
            _msaaTarget = CreateLayerMsaaTarget();
            _depth = _device.CreateTexture(new TextureDescription
            {
                Width = _width,
                Height = _height,
                Format = TextureFormat.Depth24Plus,
                RenderTarget = true,
                SampleCount = UISampleCount
            });
            _sdfBindGroup?.Dispose();
            _triangleBindGroup?.Dispose();
            _sdfBindGroup = null;
            _triangleBindGroup = null;
        }

        _sdfPipeline ??= CreateSdfPipeline();
        _trianglePipeline ??= CreateTrianglePipeline();
        _quadBuffer ??= CreateQuadBuffer();
        _viewportBuffer ??= _device.CreateBuffer(new BufferDescription { Size = 16, Usage = BufferUsage.Uniform | BufferUsage.CopyDst });

        EnsureInstanceBuffer();
        EnsureTriangleBuffer();

        _sdfBindGroup ??= _sdfPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _instanceBuffer, BufferSize = _instanceBuffer!.Size },
            new BindGroupBinding { Slot = 1, Buffer = _viewportBuffer, BufferSize = 16 }
        ]);
        _triangleBindGroup ??= _trianglePipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _viewportBuffer, BufferSize = 16 }
        ]);

        _texturedPipeline ??= CreateTexturedPipeline();
        EnsureTexturedBuffer();
        _atlasSampler ??= _device.CreateSampler(new SamplerDescription());
        // The atlas texture may have been replaced (grown); rebind when it did.
        if (_atlas is not null)
        {
            if (_atlasTextureBound != _atlas.Texture)
            {
                _texturedBindGroup?.Dispose();
                _texturedBindGroup = null;
            }
            _texturedBindGroup ??= _texturedPipeline.CreateBindGroup(
            [
                new BindGroupBinding { Slot = 0, Texture = _atlas.Texture },
                new BindGroupBinding { Slot = 1, Sampler = _atlasSampler },
                new BindGroupBinding { Slot = 2, Buffer = _viewportBuffer, BufferSize = 16 }
            ]);
            _atlasTextureBound = _atlas.Texture;
        }

        _glyphPipeline ??= CreateGlyphPipeline();
        _glyphSampler ??= _device.CreateSampler(new SamplerDescription());
        if (_glyphAtlas is not null)
        {
            if (_glyphTextureBound != _glyphAtlas.Texture)
            {
                _glyphBindGroup?.Dispose();
                _glyphBindGroup = null;
            }
            _glyphBindGroup ??= _glyphPipeline.CreateBindGroup(
            [
                new BindGroupBinding { Slot = 0, Texture = _glyphAtlas.Texture },
                new BindGroupBinding { Slot = 1, Sampler = _glyphSampler },
                new BindGroupBinding { Slot = 2, Buffer = _viewportBuffer, BufferSize = 16 }
            ]);
            _glyphTextureBound = _glyphAtlas.Texture;
        }

        _shadowPipeline ??= CreateShadowPipeline();
        EnsureShadowBuffer();
        _shadowBindGroup ??= _shadowPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _shadowBuffer, BufferSize = _shadowBuffer!.Size },
            new BindGroupBinding { Slot = 1, Buffer = _viewportBuffer, BufferSize = 16 }
        ]);

        _filterPipeline ??= CreateFilterPipeline();
        _filterSampler ??= _device.CreateSampler(new SamplerDescription());
        _filterParamsBuffer ??= _device.CreateBuffer(new BufferDescription
        {
            Size = FilterParamsSize,
            Usage = BufferUsage.Storage | BufferUsage.CopyDst
        });

        _glyphShadowPipeline ??= CreateGlyphShadowPipeline();
        EnsureGlyphShadowBuffer();
        if (_glyphAtlas is not null)
        {
            // Glyph atlas growth replaces the texture and repacks every cell.
            // Keep the shadow pass on the same texture as the main glyph pass;
            // otherwise shadows continue sampling the previous atlas while the
            // refreshed UVs point into the new one.
            if (_glyphShadowTextureBound != _glyphAtlas.Texture)
            {
                _glyphShadowBindGroup?.Dispose();
                _glyphShadowBindGroup = null;
            }
            _glyphShadowBindGroup ??= _glyphShadowPipeline.CreateBindGroup(
            [
                new BindGroupBinding { Slot = 0, Texture = _glyphAtlas.Texture },
                new BindGroupBinding { Slot = 1, Sampler = _glyphSampler },
                new BindGroupBinding { Slot = 2, Buffer = _viewportBuffer, BufferSize = 16 }
            ]);
            _glyphShadowTextureBound = _glyphAtlas.Texture;
        }
    }

    private IPipeline CreateSdfPipeline()
    {
        var source = FileSystem.Content.ReadAllText(PathUtil.Combine("Shaders", "Ui/Shape.wgsl"));
        return _device!.CreatePipeline(new PipelineDescription
        {
            ShaderSource = source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = TextureFormat.Rgba8Unorm,
            DepthFormat = TextureFormat.Depth24Plus,
            AlphaBlend = true,
            DepthWriteEnabled = false,
            DepthCompare = CompareFunction.Always,
            SampleCount = UISampleCount,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = 4 * sizeof(float),
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.ReadOnlyStorageBuffer, Stages = ShaderStage.Vertex | ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex }
                ]
            ]
        });
    }

    private IPipeline CreateTrianglePipeline()
    {
        var source = FileSystem.Content.ReadAllText(PathUtil.Combine("Shaders", "Ui/Polygon.wgsl"));
        return _device!.CreatePipeline(new PipelineDescription
        {
            ShaderSource = source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = TextureFormat.Rgba8Unorm,
            DepthFormat = TextureFormat.Depth24Plus,
            AlphaBlend = true,
            DepthWriteEnabled = false,
            DepthCompare = CompareFunction.Always,
            SampleCount = UISampleCount,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = TriVertexSize,
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x4, Offset = 2 * sizeof(float), ShaderLocation = 1 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex }
                ]
            ]
        });
    }

    private IPipeline CreateTexturedPipeline()
    {
        var source = FileSystem.Content.ReadAllText(PathUtil.Combine("Shaders", "Ui/Image.wgsl"));
        return _device!.CreatePipeline(new PipelineDescription
        {
            ShaderSource = source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = TextureFormat.Rgba8Unorm,
            DepthFormat = TextureFormat.Depth24Plus,
            AlphaBlend = true,
            DepthWriteEnabled = false,
            DepthCompare = CompareFunction.Always,
            SampleCount = UISampleCount,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = TexturedVertexSize,
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x4, Offset = 4 * sizeof(float), ShaderLocation = 2 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.Texture, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.Sampler, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 2, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex }
                ]
            ]
        });
    }

    private IPipeline CreateGlyphPipeline()
    {
        var source = FileSystem.Content.ReadAllText(PathUtil.Combine("Shaders", "Ui/Glyph.wgsl"));
        return _device!.CreatePipeline(new PipelineDescription
        {
            ShaderSource = source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = TextureFormat.Rgba8Unorm,
            DepthFormat = TextureFormat.Depth24Plus,
            AlphaBlend = true,
            DepthWriteEnabled = false,
            DepthCompare = CompareFunction.Always,
            SampleCount = UISampleCount,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = TexturedVertexSize,
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x4, Offset = 4 * sizeof(float), ShaderLocation = 2 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.Texture, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.Sampler, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 2, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex }
                ]
            ]
        });
    }

    private IPipeline CreateShadowPipeline()
    {
        var source = FileSystem.Content.ReadAllText(PathUtil.Combine("Shaders", "Ui/BoxShadow.wgsl"));
        return _device!.CreatePipeline(new PipelineDescription
        {
            ShaderSource = source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = TextureFormat.Rgba8Unorm,
            DepthFormat = TextureFormat.Depth24Plus,
            AlphaBlend = true,
            DepthWriteEnabled = false,
            DepthCompare = CompareFunction.Always,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = 4 * sizeof(float),
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.ReadOnlyStorageBuffer, Stages = ShaderStage.Vertex | ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex }
                ]
            ]
        });
    }

    private IPipeline CreateFilterPipeline()
    {
        var source = FileSystem.Content.ReadAllText(PathUtil.Combine("Shaders", "Ui/Filter.wgsl"));
        return _device!.CreatePipeline(new PipelineDescription
        {
            ShaderSource = source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = TextureFormat.Rgba8Unorm,
            AlphaBlend = true,
            DepthWriteEnabled = false,
            DepthCompare = CompareFunction.Always,
            SampleCount = UISampleCount,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = 4 * sizeof(float),
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.Texture, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.Sampler, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 2, Type = BindingType.ReadOnlyStorageBuffer, Stages = ShaderStage.Fragment }
                ]
            ]
        });
    }

    private void EnsureShadowBuffer()
    {
        var required = (ulong)Math.Max(1, _shadows.Count) * (ulong)ShadowInstanceSize;
        if (_shadowBuffer is not null && _shadowBuffer.Size >= required)
            return;
        _shadowBuffer?.Dispose();
        _shadowBindGroup?.Dispose();
        _shadowBindGroup = null;
        _shadowBuffer = _device!.CreateBuffer(new BufferDescription
        {
            Size = Math.Max(required, (ulong)ShadowInstanceSize),
            Usage = BufferUsage.Storage | BufferUsage.CopyDst
        });
    }

    private IPipeline CreateGlyphShadowPipeline()
    {
        var source = FileSystem.Content.ReadAllText(PathUtil.Combine("Shaders", "Ui/GlyphShadow.wgsl"));
        return _device!.CreatePipeline(new PipelineDescription
        {
            ShaderSource = source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = TextureFormat.Rgba8Unorm,
            DepthFormat = TextureFormat.Depth24Plus,
            AlphaBlend = true,
            DepthWriteEnabled = false,
            DepthCompare = CompareFunction.Always,
            SampleCount = UISampleCount,
            // slangc reorders vertex-input struct fields by type and renumbers
            // @location (see the comment in Ui/GlyphShadow.slang), so the emitted
            // WGSL declares position@0, softness@1, uv@2, color@3 — NOT the
            // struct field order. The attribute descriptors below map the
            // GlyphShadowVertex memory layout (position, uv, color, softness)
            // onto those emitted locations.
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = GlyphShadowVertexSize,
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32, Offset = 8 * sizeof(float), ShaderLocation = 1 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 2 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x4, Offset = 4 * sizeof(float), ShaderLocation = 3 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.Texture, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.Sampler, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 2, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex }
                ]
            ]
        });
    }

    private void EnsureGlyphShadowBuffer()
    {
        var required = (ulong)Math.Max(1, _glyphShadows.Count) * (ulong)GlyphShadowVertexSize;
        if (_glyphShadowBuffer is not null && _glyphShadowBuffer.Size >= required)
            return;
        _glyphShadowBuffer?.Dispose();
        _glyphShadowBuffer = _device!.CreateBuffer(new BufferDescription
        {
            Size = Math.Max(required, (ulong)GlyphShadowVertexSize),
            Usage = BufferUsage.Vertex | BufferUsage.CopyDst
        });
    }

    private IBuffer CreateQuadBuffer()
    {
        // Unit quad (position + uv), six vertices, used by every SDF instance.
        float[] vertices =
        [
            0f, 0f, 0f, 0f,
            1f, 0f, 1f, 0f,
            1f, 1f, 1f, 1f,
            0f, 0f, 0f, 0f,
            1f, 1f, 1f, 1f,
            0f, 1f, 0f, 1f
        ];
        var buffer = _device!.CreateBuffer(new BufferDescription
        {
            Size = (ulong)(vertices.Length * sizeof(float)),
            Usage = BufferUsage.Vertex | BufferUsage.CopyDst
        });
        unsafe
        {
            fixed (float* data = vertices)
                buffer.Write(new ReadOnlySpan<byte>(data, vertices.Length * sizeof(float)));
        }
        return buffer;
    }

    private void EnsureInstanceBuffer()
    {
        var required = (ulong)Math.Max(1, _instances.Count) * (ulong)SdfInstanceSize;
        if (_instanceBuffer is not null && _instanceBuffer.Size >= required)
            return;
        _instanceBuffer?.Dispose();
        _sdfBindGroup?.Dispose();
        _sdfBindGroup = null;
        _instanceBuffer = _device!.CreateBuffer(new BufferDescription
        {
            Size = Math.Max(required, (ulong)SdfInstanceSize),
            Usage = BufferUsage.Storage | BufferUsage.CopyDst
        });
    }

    private void EnsureTriangleBuffer()
    {
        var required = (ulong)Math.Max(1, _triangles.Count) * (ulong)TriVertexSize;
        if (_triangleBuffer is not null && _triangleBuffer.Size >= required)
            return;
        _triangleBuffer?.Dispose();
        _triangleBuffer = _device!.CreateBuffer(new BufferDescription
        {
            Size = Math.Max(required, (ulong)TriVertexSize),
            Usage = BufferUsage.Vertex | BufferUsage.CopyDst
        });
    }

    private void EnsureTexturedBuffer()
    {
        var required = (ulong)Math.Max(1, _textured.Count) * (ulong)TexturedVertexSize;
        if (_texturedBuffer is not null && _texturedBuffer.Size >= required)
            return;
        _texturedBuffer?.Dispose();
        _texturedBuffer = _device!.CreateBuffer(new BufferDescription
        {
            Size = Math.Max(required, (ulong)TexturedVertexSize),
            Usage = BufferUsage.Vertex | BufferUsage.CopyDst
        });
    }

    private void Submit()
    {
        using ICommandBuffer commandBuffer = _device!.CreateCommandBuffer();
        _viewportBuffer!.Write(new Vector4(_width, _height, 1f / _width, 1f / _height));

        EnsureInstanceBuffer();
        EnsureTriangleBuffer();
        EnsureTexturedBuffer();
        EnsureShadowBuffer();
        EnsureGlyphShadowBuffer();
        Upload(_instances, _instanceBuffer!);
        Upload(_triangles, _triangleBuffer!);
        Upload(_textured, _texturedBuffer!);
        Upload(_shadows, _shadowBuffer!);
        Upload(_glyphShadows, _glyphShadowBuffer!);

        RenderLayer(
            commandBuffer, _target!, _msaaTarget!, _depth!,
            _instances, _triangles, _textured, _shadows, _glyphShadows, _commands,
            _instanceBuffer!, _triangleBuffer!, _texturedBuffer!, _shadowBuffer!, _glyphShadowBuffer!,
            _sdfBindGroup!, _shadowBindGroup!, _glyphShadowBindGroup!);
        commandBuffer.Submit();
    }

    private static void Upload<T>(List<T> items, IBuffer buffer) where T : unmanaged
    {
        if (items.Count == 0)
            return;
        var span = CollectionsMarshal.AsSpan(items);
        buffer.Write(MemoryMarshal.AsBytes(span));
    }

    private void RenderLayer(
        ICommandBuffer commandBuffer, ITexture target, ITexture msaaTarget, ITexture depth,
        List<SdfInstance> instances, List<TriVertex> triangles, List<TexturedVertex> textured,
        List<ShadowInstance> shadows, List<GlyphShadowVertex> glyphShadows, List<DrawCmd> commands,
        IBuffer instanceBuffer, IBuffer triangleBuffer, IBuffer texturedBuffer, IBuffer shadowBuffer, IBuffer glyphShadowBuffer,
        IBindGroup sdfBindGroup, IBindGroup shadowBindGroup, IBindGroup glyphShadowBindGroup)
    {
        IRenderPass? pass = BeginPass(commandBuffer, msaaTarget, target, depth, clear: true);
        IPipeline? currentPipeline = null;

        foreach (var command in commands)
        {
            if (command.Count <= 0)
                continue;

            if (command.Kind == BatchKind.FilterBlit)
            {
                pass.End();
                pass.Dispose();
                pass = null;
                currentPipeline = null;

                var child = _filterLayers[command.Start];

                child.InstanceBuffer = EnsureBuffer(child.InstanceBuffer, (ulong)Math.Max(1, child.Instances.Count) * (ulong)SdfInstanceSize, BufferUsage.Storage | BufferUsage.CopyDst, (ulong)SdfInstanceSize);
                child.TriangleBuffer = EnsureBuffer(child.TriangleBuffer, (ulong)Math.Max(1, child.Triangles.Count) * (ulong)TriVertexSize, BufferUsage.Vertex | BufferUsage.CopyDst, (ulong)TriVertexSize);
                child.TexturedBuffer = EnsureBuffer(child.TexturedBuffer, (ulong)Math.Max(1, child.Textured.Count) * (ulong)TexturedVertexSize, BufferUsage.Vertex | BufferUsage.CopyDst, (ulong)TexturedVertexSize);
                child.ShadowBuffer = EnsureBuffer(child.ShadowBuffer, (ulong)Math.Max(1, child.Shadows.Count) * (ulong)ShadowInstanceSize, BufferUsage.Storage | BufferUsage.CopyDst, (ulong)ShadowInstanceSize);
                child.GlyphShadowBuffer = EnsureBuffer(child.GlyphShadowBuffer, (ulong)Math.Max(1, child.GlyphShadows.Count) * (ulong)GlyphShadowVertexSize, BufferUsage.Vertex | BufferUsage.CopyDst, (ulong)GlyphShadowVertexSize);
                child.SdfBindGroup ??= CreateSdfBindGroup(child.InstanceBuffer);
                child.ShadowBindGroup ??= CreateShadowBindGroup(child.ShadowBuffer);
                child.GlyphShadowBindGroup ??= CreateGlyphShadowBindGroup();
                Upload(child.Instances, child.InstanceBuffer);
                Upload(child.Triangles, child.TriangleBuffer);
                Upload(child.Textured, child.TexturedBuffer);
                Upload(child.Shadows, child.ShadowBuffer);
                Upload(child.GlyphShadows, child.GlyphShadowBuffer);

                RenderLayer(
                    commandBuffer, child.Target!, child.MsaaTarget!, child.Depth!,
                    child.Instances, child.Triangles, child.Textured, child.Shadows, child.GlyphShadows, child.Commands,
                    child.InstanceBuffer, child.TriangleBuffer, child.TexturedBuffer, child.ShadowBuffer, child.GlyphShadowBuffer,
                    child.SdfBindGroup, child.ShadowBindGroup, child.GlyphShadowBindGroup);

                BlitFilter(commandBuffer, msaaTarget, target, depth, child);
                pass = BeginPass(commandBuffer, msaaTarget, target, depth, clear: false);
                continue;
            }

            DrawCommand(pass, command, instanceBuffer, triangleBuffer, texturedBuffer, shadowBuffer, glyphShadowBuffer, sdfBindGroup, shadowBindGroup, glyphShadowBindGroup, ref currentPipeline);
        }

        pass.End();
        pass.Dispose();
    }

    private void DrawCommand(
        IRenderPass pass, DrawCmd command,
        IBuffer instanceBuffer, IBuffer triangleBuffer, IBuffer texturedBuffer, IBuffer shadowBuffer, IBuffer glyphShadowBuffer,
        IBindGroup sdfBindGroup, IBindGroup shadowBindGroup, IBindGroup glyphShadowBindGroup, ref IPipeline? currentPipeline)
    {
        switch (command.Kind)
        {
            case BatchKind.Sdf:
                if (currentPipeline != _sdfPipeline)
                {
                    pass.SetPipeline(_sdfPipeline!);
                    pass.SetBindGroup(sdfBindGroup, 0);
                    currentPipeline = _sdfPipeline;
                }
                pass.SetVertexBuffer(_quadBuffer!, (ulong)(6 * 4 * sizeof(float)));
                pass.DrawInstanced(6, (uint)command.Count, (uint)command.Start);
                break;
            case BatchKind.Textured:
                if (currentPipeline != _texturedPipeline)
                {
                    pass.SetPipeline(_texturedPipeline!);
                    pass.SetBindGroup(_texturedBindGroup!, 0);
                    currentPipeline = _texturedPipeline;
                }
                pass.SetVertexBuffer(texturedBuffer, texturedBuffer.Size);
                pass.Draw((uint)command.Count, (uint)command.Start);
                break;
            case BatchKind.Glyph:
                if (currentPipeline != _glyphPipeline)
                {
                    pass.SetPipeline(_glyphPipeline!);
                    pass.SetBindGroup(_glyphBindGroup!, 0);
                    currentPipeline = _glyphPipeline;
                }
                pass.SetVertexBuffer(texturedBuffer, texturedBuffer.Size);
                pass.Draw((uint)command.Count, (uint)command.Start);
                break;
            case BatchKind.Shadow:
                if (currentPipeline != _shadowPipeline)
                {
                    pass.SetPipeline(_shadowPipeline!);
                    pass.SetBindGroup(shadowBindGroup, 0);
                    currentPipeline = _shadowPipeline;
                }
                pass.SetVertexBuffer(_quadBuffer!, (ulong)(6 * 4 * sizeof(float)));
                pass.DrawInstanced(6, (uint)command.Count, (uint)command.Start);
                break;
            case BatchKind.GlyphShadow:
                if (currentPipeline != _glyphShadowPipeline)
                {
                    pass.SetPipeline(_glyphShadowPipeline!);
                    pass.SetBindGroup(glyphShadowBindGroup, 0);
                    currentPipeline = _glyphShadowPipeline;
                }
                pass.SetVertexBuffer(glyphShadowBuffer, glyphShadowBuffer.Size);
                pass.Draw((uint)command.Count, (uint)command.Start);
                break;
            default:
                if (currentPipeline != _trianglePipeline)
                {
                    pass.SetPipeline(_trianglePipeline!);
                    pass.SetBindGroup(_triangleBindGroup!, 0);
                    currentPipeline = _trianglePipeline;
                }
                pass.SetVertexBuffer(triangleBuffer, triangleBuffer.Size);
                pass.Draw((uint)command.Count, (uint)command.Start);
                break;
        }
    }

    private IRenderPass BeginPass(ICommandBuffer commandBuffer, ITexture msaaTarget, ITexture target, ITexture depth, bool clear)
    {
        var loadOp = clear ? RenderAttachmentLoadOp.Clear : RenderAttachmentLoadOp.Load;
        return commandBuffer.BeginRenderPass(new RenderPassDescription
        {
            Color = new ColorAttachment
            {
                // Render into the multisampled target and resolve into the
                // single-sample target, which is what later passes sample.
                Texture = msaaTarget,
                ResolveTarget = target,
                LoadOp = loadOp,
                StoreOp = RenderAttachmentStoreOp.Store,
                ClearColor = new Vector4(0f, 0f, 0f, 0f)
            },
            Depth = new DepthAttachment
            {
                Texture = depth,
                LoadOp = loadOp,
                StoreOp = RenderAttachmentStoreOp.Store,
                ClearValue = 1f
            }
        });
    }

    private void BlitFilter(ICommandBuffer commandBuffer, ITexture msaaTarget, ITexture target, ITexture depth, FilterLayer child)
    {
        _filterParamsBuffer!.Write(EncodeFilterParams(child.Filter));
        child.FilterBindGroup ??= _filterPipeline!.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Texture = child.Target! },
            new BindGroupBinding { Slot = 1, Sampler = _filterSampler },
            new BindGroupBinding { Slot = 2, Buffer = _filterParamsBuffer, BufferSize = _filterParamsBuffer.Size }
        ]);

        using IRenderPass pass = BeginPass(commandBuffer, msaaTarget, target, depth, clear: false);
        pass.SetPipeline(_filterPipeline!);
        pass.SetBindGroup(child.FilterBindGroup, 0);
        pass.SetVertexBuffer(_quadBuffer!, (ulong)(6 * 4 * sizeof(float)));
        pass.Draw(6);
        pass.End();
    }

    internal static FilterParams EncodeFilterParams(Filter2D filter)
    {
        var result = default(FilterParams);
        var count = Math.Min(8, filter.Ops.Count);
        var ops = new Vector4[8];
        for (var i = 0; i < count; i++)
        {
            var op = filter.Ops[i];
            if (op.Kind == FilterOpKind.Blur)
                result.Blur = new Vector4(op.Amount, 0f, 0f, 0f);
            ops[i] = new Vector4((float)op.Kind, op.Amount, 0f, 0f);
        }
        result.Op0 = ops[0];
        result.Op1 = ops[1];
        result.Op2 = ops[2];
        result.Op3 = ops[3];
        result.Op4 = ops[4];
        result.Op5 = ops[5];
        result.Op6 = ops[6];
        result.Op7 = ops[7];
        result.OpCount = new Vector4(count, 0f, 0f, 0f);
        return result;
    }

    private IBindGroup CreateSdfBindGroup(IBuffer instanceBuffer) => _sdfPipeline!.CreateBindGroup(
    [
        new BindGroupBinding { Slot = 0, Buffer = instanceBuffer, BufferSize = instanceBuffer.Size },
        new BindGroupBinding { Slot = 1, Buffer = _viewportBuffer, BufferSize = 16 }
    ]);

    private IBindGroup CreateShadowBindGroup(IBuffer shadowBuffer) => _shadowPipeline!.CreateBindGroup(
    [
        new BindGroupBinding { Slot = 0, Buffer = shadowBuffer, BufferSize = shadowBuffer.Size },
        new BindGroupBinding { Slot = 1, Buffer = _viewportBuffer, BufferSize = 16 }
    ]);

    private IBindGroup CreateGlyphShadowBindGroup() => _glyphShadowPipeline!.CreateBindGroup(
    [
        new BindGroupBinding { Slot = 0, Texture = _glyphAtlas!.Texture },
        new BindGroupBinding { Slot = 1, Sampler = _glyphSampler },
        new BindGroupBinding { Slot = 2, Buffer = _viewportBuffer, BufferSize = 16 }
    ]);

    private IBuffer EnsureBuffer(IBuffer? buffer, ulong required, BufferUsage usage, ulong minimum)
    {
        if (buffer is not null && buffer.Size >= required)
            return buffer;
        buffer?.Dispose();
        return _device!.CreateBuffer(new BufferDescription
        {
            Size = Math.Max(required, minimum),
            Usage = usage
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _target?.Dispose();
        _msaaTarget?.Dispose();
        _depth?.Dispose();
        _quadBuffer?.Dispose();
        _instanceBuffer?.Dispose();
        _triangleBuffer?.Dispose();
        _texturedBuffer?.Dispose();
        _viewportBuffer?.Dispose();
        _sdfPipeline?.Dispose();
        _trianglePipeline?.Dispose();
        _texturedPipeline?.Dispose();
        _sdfBindGroup?.Dispose();
        _triangleBindGroup?.Dispose();
        _texturedBindGroup?.Dispose();
        _atlasSampler?.Dispose();
        _atlas?.Dispose();
        _glyphPipeline?.Dispose();
        _glyphBindGroup?.Dispose();
        _glyphSampler?.Dispose();
        _glyphAtlas?.Dispose();
        _shadowBuffer?.Dispose();
        _shadowPipeline?.Dispose();
        _shadowBindGroup?.Dispose();
        _filterPipeline?.Dispose();
        _filterSampler?.Dispose();
        _filterParamsBuffer?.Dispose();
        _glyphShadowBuffer?.Dispose();
        _glyphShadowPipeline?.Dispose();
        _glyphShadowBindGroup?.Dispose();
    }

    /// <summary>Packed gradient parameters baked inline per shape.</summary>
    private readonly struct GradientData
    {
        public readonly float Kind; // 0 solid, 1 linear, 2 radial
        public readonly Vector4 A;
        public readonly Vector4 B;
        public readonly Vector4 Stop0;
        public readonly Vector4 Stop1;
        public readonly Vector4 Stop2;
        public readonly Vector4 Stop3;
        public readonly Vector4 Offsets;
        public readonly int StopCount;

        public static readonly GradientData None = default;

        private GradientData(float kind, Vector4 a, Vector4 b, in Vector4 offsets, in Vector4 s0, in Vector4 s1, in Vector4 s2, in Vector4 s3, int stopCount)
        {
            Kind = kind;
            A = a;
            B = b;
            Offsets = offsets;
            Stop0 = s0;
            Stop1 = s1;
            Stop2 = s2;
            Stop3 = s3;
            StopCount = stopCount;
        }

        public static GradientData FromLinear(in LinearGradient gradient)
        {
            Normalize(gradient.Stop0, gradient.Stop1, gradient.Stop2, gradient.Stop3, gradient.StopCount,
                out var offsets, out var s0, out var s1, out var s2, out var s3, out var count);
            return new GradientData(1f, new Vector4(gradient.Start.X, gradient.Start.Y, 0f, 0f),
                new Vector4(gradient.End.X, gradient.End.Y, 0f, 0f), offsets, s0, s1, s2, s3, count);
        }

        public static GradientData FromRadial(in RadialGradient gradient)
        {
            Normalize(gradient.Stop0, gradient.Stop1, gradient.Stop2, gradient.Stop3, gradient.StopCount,
                out var offsets, out var s0, out var s1, out var s2, out var s3, out var count);
            return new GradientData(2f, new Vector4(gradient.Center.X, gradient.Center.Y, 0f, 0f),
                new Vector4(gradient.Radius, 0f, 0f, 0f), offsets, s0, s1, s2, s3, count);
        }

        private static void Normalize(in GradientStop s0, in GradientStop s1, in GradientStop s2, in GradientStop s3, int stopCount,
            out Vector4 offsets, out Vector4 c0, out Vector4 c1, out Vector4 c2, out Vector4 c3, out int count)
        {
            var stops = new[] { s0, s1, s2, s3 };
            count = Math.Min(MaxStops, Math.Max(0, stopCount));
            // Ascending offsets (insertion sort over at most four elements).
            for (var i = 1; i < count; i++)
            {
                var j = i;
                while (j > 0 && stops[j - 1].Offset > stops[j].Offset)
                {
                    (stops[j - 1], stops[j]) = (stops[j], stops[j - 1]);
                    j--;
                }
            }
            Span<float> offs = stackalloc float[4];
            for (var i = 0; i < count; i++)
                offs[i] = Math.Clamp(stops[i].Offset, 0f, 1f);
            offsets = new Vector4(offs[0], offs[1], offs[2], offs[3]);
            c0 = stops[0].Color.ToVector4();
            c1 = count > 1 ? stops[1].Color.ToVector4() : Vector4.Zero;
            c2 = count > 2 ? stops[2].Color.ToVector4() : Vector4.Zero;
            c3 = count > 3 ? stops[3].Color.ToVector4() : Vector4.Zero;
        }
    }
}
