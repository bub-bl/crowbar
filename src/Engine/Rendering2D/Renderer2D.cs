using System.Numerics;
using System.Runtime.InteropServices;
using Crowbar.Engine.Rendering;
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
    Glyph = 3
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
/// One instanced SDF shape. The layout mirrors <c>Shaders/SdfShape.wgsl</c>
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
    public Vector4 Params;    // x = corner radius, y = stroke width (0 = fill), z/w unused
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
/// color). Mirrors <c>Shaders/TriMesh.wgsl</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TriVertex
{
    public Vector2 Position;
    public Vector4 Color;
}

/// <summary>
/// One textured-quad vertex (screen-space position + atlas UV + straight sRGB
/// tint). Mirrors <c>Shaders/Textured.wgsl</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TexturedVertex
{
    public Vector2 Position;
    public Vector2 Uv;
    public Vector4 Color;
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

    /// <summary>A cached glyph atlas entry: the packed SDF and its ink size.</summary>
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

    // Recorded batches.
    private readonly List<SdfInstance> _instances = [];
    private readonly List<TriVertex> _triangles = [];
    private readonly List<TexturedVertex> _textured = [];
    private readonly List<DrawCmd> _commands = [];
    private bool _runOpen;
    private BatchKind _runKind;
    private int _runStart;

    // Reusable scratch for polygon tessellation (no per-frame allocation).
    private readonly List<Vector2> _polyScratch = [];
    private readonly List<Vector2> _polyClipA = [];
    private readonly List<Vector2> _polyClipB = [];
    private readonly List<int> _earIndices = [];

    // GPU resources (created lazily; null when headless).
    private ITexture? _target;
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
    private IPipeline? _glyphPipeline;
    private IBindGroup? _glyphBindGroup;
    private ITexture? _glyphTextureBound;
    private ISampler? _glyphSampler;

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

    /// <summary>Number of contiguous draw runs the current frame will emit (diagnostics/tests).</summary>
    public int BatchCount => _commands.Count + (_runOpen ? 1 : 0);

    // Internal access for unit tests and in-process diagnostics.
    internal IReadOnlyList<SdfInstance> Instances => _instances;
    internal IReadOnlyList<TriVertex> Triangles => _triangles;
    internal IReadOnlyList<TexturedVertex> TexturedVerts => _textured;
    internal IReadOnlyList<DrawCmd> Commands => _commands;
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
        _commands.Clear();
        _transforms.Clear();
        _transformTop = -1;
        _current = Matrix3x2.Identity;
        _clips.Clear();
        _runOpen = false;
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
        if (_runOpen)
        {
            _commands.Add(new DrawCmd(_runKind, _runStart, RunLength(_runKind) - _runStart));
            _runOpen = false;
        }

        if (_device is null || _commands.Count == 0)
            return _target;

        EnsureGpuResources();
        Upload();
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

    /// <summary>Draws a stroked rounded rect (a solid border of <paramref name="strokeWidth"/>).</summary>
    public void DrawRoundedRect(RectF rect, float radius, float strokeWidth, ColorF color) =>
        EmitShape(ShapeKind.RoundedRect, rect, radius, strokeWidth, color, GradientData.None);

    /// <summary>Draws a stroked circle (a ring of <paramref name="strokeWidth"/>).</summary>
    public void DrawCircle(Vector2 center, float radius, float strokeWidth, ColorF color) =>
        EmitShape(ShapeKind.Ellipse, CircleRect(center, radius), 0f, strokeWidth, color, GradientData.None);

    /// <summary>Draws a stroked ellipse.</summary>
    public void DrawEllipse(RectF bounds, float strokeWidth, ColorF color) =>
        EmitShape(ShapeKind.Ellipse, bounds, 0f, strokeWidth, color, GradientData.None);

    /// <summary>Draws a line segment with round caps.</summary>
    public void DrawLine(Vector2 a, Vector2 b, float width, ColorF color) =>
        EmitLine(a, b, width, color);

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
        var options = new TextOptions(resolved.CreateFont(style.FontSize))
        {
            Dpi = 72,
            KerningMode = KerningMode.Standard,
            ColorFontSupport = ColorFontSupport.None
        };
        if (style.MaxWidth > 0f)
            options.WrappingLength = style.MaxWidth;
        if (style.LineHeight > 0f)
            options.LineSpacing = style.LineHeight;
        if (style.LetterSpacing != 0f)
            options.Tracking = style.LetterSpacing / style.FontSize;

        if (style.Align != TextAlign.Left)
        {
            options.TextAlignment = style.Align == TextAlign.Center ? TextAlignment.Center : TextAlignment.End;
            if (style.MaxWidth > 0f)
            {
                var advance = TextMeasurer.MeasureAdvance(text, options).Width;
                var offset = style.Align == TextAlign.Center ? (style.MaxWidth - advance) * 0.5f : style.MaxWidth - advance;
                options.Origin = new Vector2(position.X + offset, position.Y);
            }
            else
            {
                options.Origin = position;
            }
        }
        else
        {
            options.Origin = position;
        }

        _glyphCollector.Configure(style.Color, resolved.Key, (int)MathF.Round(style.FontSize));
        TextRenderer.RenderTo(_glyphCollector, text, options);
    }

    /// <summary>
    /// Called by the glyph collector for each laid-out glyph: rasterizes the
    /// outline into the glyph atlas (cached) and emits its SDF quad.
    /// </summary>
    internal void EmitGlyph(IReadOnlyList<Vector2> edges, ColorF color, string fontKey, int sizeKey, ushort glyphId)
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

        var uv = entry.Atlas.UvRect;
        var colorVector = color.ToVector4();
        var x0 = min.X - GlyphRasterizer.Padding;
        var y0 = min.Y - GlyphRasterizer.Padding;
        var x1 = x0 + entry.Atlas.Width;
        var y1 = y0 + entry.Atlas.Height;
        var uv0 = new Vector2(uv.X, uv.Y);
        var uv1 = new Vector2(uv.Right, uv.Y);
        var uv2 = new Vector2(uv.Right, uv.Bottom);
        var uv3 = new Vector2(uv.X, uv.Bottom);

        EnsureRun(BatchKind.Glyph);
        _textured.Add(new TexturedVertex { Position = new Vector2(x0, y0), Uv = uv0, Color = colorVector });
        _textured.Add(new TexturedVertex { Position = new Vector2(x1, y0), Uv = uv1, Color = colorVector });
        _textured.Add(new TexturedVertex { Position = new Vector2(x1, y1), Uv = uv2, Color = colorVector });
        _textured.Add(new TexturedVertex { Position = new Vector2(x0, y0), Uv = uv0, Color = colorVector });
        _textured.Add(new TexturedVertex { Position = new Vector2(x1, y1), Uv = uv2, Color = colorVector });
        _textured.Add(new TexturedVertex { Position = new Vector2(x0, y1), Uv = uv3, Color = colorVector });
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
    // Recording internals
    // ---------------------------------------------------------------------

    private static RectF CircleRect(Vector2 center, float radius) =>
        new(center.X - radius, center.Y - radius, radius * 2f, radius * 2f);

    private void EmitLine(Vector2 a, Vector2 b, float width, ColorF color)
    {
        var half = MathF.Max(width, 0f) * 0.5f;
        var min = Vector2.Min(a, b) - new Vector2(half);
        var max = Vector2.Max(a, b) + new Vector2(half);
        var rect = RectF.FromLTRB(min.X, min.Y, max.X, max.Y);
        var instance = BeginInstance(ShapeKind.Line, rect);
        instance.Params = new Vector4(0f, MathF.Max(width, 0f), 0f, 0f);
        instance.Color = color.ToVector4();
        instance.Grad0 = new Vector4(a.X, a.Y, 0f, 0f);
        instance.Grad1 = new Vector4(b.X, b.Y, 0f, 0f);
        instance.Flags = new Vector4((float)ShapeKind.Line, 0f, ClipCount(), 0f);
        AddInstance(instance);
    }

    private void EmitShape(ShapeKind kind, RectF rect, float radius, float strokeWidth, ColorF color, in GradientData gradient)
    {
        if (rect.IsEmpty)
            return;
        var instance = BeginInstance(kind, rect);
        instance.Params = new Vector4(radius, MathF.Max(strokeWidth, 0f), 0f, 0f);
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
            _depth = _device.CreateTexture(new TextureDescription
            {
                Width = _width,
                Height = _height,
                Format = TextureFormat.Depth24Plus,
                RenderTarget = true
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
    }

    private IPipeline CreateSdfPipeline()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "SdfShape.wgsl"));
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

    private IPipeline CreateTrianglePipeline()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "TriMesh.wgsl"));
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
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Textured.wgsl"));
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
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Glyph.wgsl"));
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

    private void Upload()
    {
        if (_instances.Count > 0)
        {
            var span = CollectionsMarshal.AsSpan(_instances);
            _instanceBuffer!.Write(MemoryMarshal.AsBytes(span));
        }
        if (_triangles.Count > 0)
        {
            var span = CollectionsMarshal.AsSpan(_triangles);
            _triangleBuffer!.Write(MemoryMarshal.AsBytes(span));
        }
        if (_textured.Count > 0)
        {
            var span = CollectionsMarshal.AsSpan(_textured);
            _texturedBuffer!.Write(MemoryMarshal.AsBytes(span));
        }
        _viewportBuffer!.Write(new Vector4(_width, _height, 1f / _width, 1f / _height));
    }

    private void Submit()
    {
        using ICommandBuffer commandBuffer = _device!.CreateCommandBuffer();
        using IRenderPass pass = commandBuffer.BeginRenderPass(new RenderPassDescription
        {
            Color = new ColorAttachment
            {
                Texture = _target!,
                LoadOp = RenderAttachmentLoadOp.Clear,
                StoreOp = RenderAttachmentStoreOp.Store,
                ClearColor = new Vector4(0f, 0f, 0f, 0f)
            },
            Depth = new DepthAttachment
            {
                Texture = _depth!,
                LoadOp = RenderAttachmentLoadOp.Clear,
                StoreOp = RenderAttachmentStoreOp.Store,
                ClearValue = 1f
            }
        });

        IPipeline? currentPipeline = null;
        foreach (var command in _commands)
        {
            if (command.Count <= 0)
                continue;
            if (command.Kind == BatchKind.Sdf)
            {
                if (currentPipeline != _sdfPipeline)
                {
                    pass.SetPipeline(_sdfPipeline!);
                    pass.SetBindGroup(_sdfBindGroup!, 0);
                    currentPipeline = _sdfPipeline;
                }
                pass.SetVertexBuffer(_quadBuffer!, (ulong)(6 * 4 * sizeof(float)));
                pass.DrawInstanced(6, (uint)command.Count);
            }
            else if (command.Kind == BatchKind.Textured)
            {
                if (currentPipeline != _texturedPipeline)
                {
                    pass.SetPipeline(_texturedPipeline!);
                    pass.SetBindGroup(_texturedBindGroup!, 0);
                    currentPipeline = _texturedPipeline;
                }
                pass.SetVertexBuffer(_texturedBuffer!, _texturedBuffer!.Size);
                pass.Draw((uint)command.Count);
            }
            else if (command.Kind == BatchKind.Glyph)
            {
                if (currentPipeline != _glyphPipeline)
                {
                    pass.SetPipeline(_glyphPipeline!);
                    pass.SetBindGroup(_glyphBindGroup!, 0);
                    currentPipeline = _glyphPipeline;
                }
                pass.SetVertexBuffer(_texturedBuffer!, _texturedBuffer!.Size);
                pass.Draw((uint)command.Count);
            }
            else
            {
                if (currentPipeline != _trianglePipeline)
                {
                    pass.SetPipeline(_trianglePipeline!);
                    pass.SetBindGroup(_triangleBindGroup!, 0);
                    currentPipeline = _trianglePipeline;
                }
                pass.SetVertexBuffer(_triangleBuffer!, _triangleBuffer!.Size);
                pass.Draw((uint)command.Count);
            }
        }

        pass.End();
        commandBuffer.Submit();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _target?.Dispose();
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
