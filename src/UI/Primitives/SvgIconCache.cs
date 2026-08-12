using System.Text.RegularExpressions;
using SkiaSharp;
using Svg.Skia;

namespace Crowbar.UI;

/// <summary>
/// Rasterizes the SVG icons used by the <c>&lt;icon name="..."&gt;</c> panel
/// into tinted bitmaps. The source of truth is the SVG pack in
/// <c>Assets/Icons/&lt;name&gt;.svg</c> (the same convention as the engine's
/// <c>Assets/Gizmos</c>), so adding or swapping an icon is just a file change.
///
/// The pack colors are normalized to the panel's computed <c>color</c> before
/// rasterization, so an icon tints like text (hover, disabled and active
/// states fall out of the CSS color pipeline): both <c>currentColor</c>
/// (Crowbar icons) and hardcoded hex fills/strokes (the Solar pack) are
/// rewritten to the tint. Rasters are cached by (name, color, size): the
/// editor theme uses a handful of colors at a handful of sizes, so the cache
/// stays small. A missing or malformed SVG never throws — it simply yields no
/// raster and the panel draws nothing.
/// </summary>
public sealed class SvgIconCache
{
    /// <summary>The process-wide cache used by the renderer by default.</summary>
    public static SvgIconCache Shared { get; } = new();

    /// <summary>Directory icon names are resolved against (defaults to <c>Assets/Icons</c> in the app base directory).</summary>
    public string ContentRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons");

    private readonly Dictionary<string, SKImage> _raster = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (float Width, float Height)> _intrinsic = new(StringComparer.OrdinalIgnoreCase);
    // The shared cache is used by every renderer in the process (tests render
    // in parallel, and hosts may load icons off the render thread); guard the
    // dictionaries so concurrent Get/Clear never corrupt them.
    private readonly object _lock = new();

    /// <summary>
    /// Gets the icon rasterized to exactly <paramref name="width"/> x
    /// <paramref name="height"/> pixels and tinted with
    /// <paramref name="tint"/>, or null when the icon cannot be loaded.
    /// Rasterizing at the exact target size keeps icons crisp without
    /// upscaling; caching by (name, color, size) means the per-frame paint
    /// pass never decodes twice.
    /// </summary>
    public SKImage? Get(string name, SKColor tint, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(name) || width <= 0 || height <= 0) return null;
        var path = Resolve(name);
        if (path is null) return null;

        var key = $"{path}|{tint.Red}-{tint.Green}-{tint.Blue}|{width}x{height}";
        lock (_lock)
        {
            if (_raster.TryGetValue(key, out var cached)) return cached;
        }

        using var source = SvgSource.Load(path, tint);
        if (source is null) return null;

        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            DrawPictureFit(canvas, source.Picture, width, height);
            canvas.Flush();
        }

        // SKImage.FromBitmap keeps the bitmap's pixels alive: the image is the
        // cache entry and owns them until Clear(). A concurrent duplicate
        // decode simply replaces the entry; the loser is still returned to its
        // caller and reclaimed once unused.
        var image = SKImage.FromBitmap(bitmap);
        lock (_lock) _raster[key] = image;
        return image;
    }

    /// <summary>
    /// Gets the intrinsic size (in the SVG's own units) of an icon, used by
    /// the layout engine to derive the aspect ratio of an <c>&lt;icon&gt;</c>
    /// without explicit dimensions. Returns false when the icon is unknown.
    /// </summary>
    public bool TryGetIntrinsicSize(string name, out float width, out float height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var path = Resolve(name);
        if (path is null) return false;
        lock (_lock)
        {
            if (_intrinsic.TryGetValue(path, out var known))
            {
                width = known.Width;
                height = known.Height;
                return true;
            }
        }

        // Intrinsic size does not depend on the tint: any opaque color works.
        using var source = SvgSource.Load(path, SKColors.White);
        if (source is null) return false;
        var bounds = source.Picture.CullRect;
        if (bounds.Width <= 0f || bounds.Height <= 0f) return false;
        width = bounds.Width;
        height = bounds.Height;
        lock (_lock) _intrinsic[path] = (width, height);
        return true;
    }

    /// <summary>Drops every cached raster and intrinsic measurement.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var image in _raster.Values) image.Dispose();
            _raster.Clear();
            _intrinsic.Clear();
        }
    }

    // Subfolder paths are allowed (e.g. the Solar pack lives in
    // Assets/Icons/Solar/&lt;category&gt;/Bold/&lt;name&gt;.svg), so '/' is a valid name
    // char; every other file-name-invalid char (and '..' / leading '/') is
    // rejected so a name can never escape the icon directory.
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars()
        .Where(c => c is not '/' and not '\\')
        .ToArray();

    // Solar (and most packs) hardcode fill/stroke colors instead of using
    // currentColor: replace every concrete #hex fill/stroke with currentColor
    // so the whole pack tints through the computed color. fill="none" is left
    // alone (stroke-based icons keep their strokes).
    private static readonly Regex HexColorAttribute = new(
        "(fill|stroke)=\"#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?\"", RegexOptions.Compiled);

    private string? Resolve(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.IndexOfAny(InvalidNameChars) >= 0) return null;
        var path = Path.Combine(ContentRoot, normalized + ".svg");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// A parsed SVG that owns its <see cref="SKSvg"/>: disposing it frees the
    /// native picture. The picture must never outlive the holder (SKSvg
    /// disposes the picture it loaded), so callers use it within a using.
    /// </summary>
    private sealed class SvgSource : IDisposable
    {
        private SvgSource(SKSvg svg) => Svg = svg;

        public SKSvg Svg { get; }
        public SKPicture Picture => Svg.Picture!;

        public static SvgSource? Load(string path, SKColor tint)
        {
            try
            {
                // The packs use currentColor (Crowbar icons) or hardcoded hex
                // fills/strokes (Solar): normalize both to the requested tint so
                // icons tint through the computed color.
                var source = HexColorAttribute.Replace(File.ReadAllText(path), "$1=\"currentColor\"")
                    .Replace("currentColor", $"#{tint.Red:X2}{tint.Green:X2}{tint.Blue:X2}", StringComparison.Ordinal);
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(source));
                var svg = new SKSvg();
                if (svg.Load(stream) is null || svg.Picture is null)
                {
                    svg.Dispose();
                    return null;
                }
                return new SvgSource(svg);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A missing, locked or malformed icon must never crash the UI:
                // the paint pass simply draws nothing for that panel.
                return null;
            }
        }

        public void Dispose() => Svg.Dispose();
    }

    private static void DrawPictureFit(SKCanvas canvas, SKPicture picture, float width, float height)
    {
        var bounds = picture.CullRect;
        if (bounds.Width <= 0f || bounds.Height <= 0f) return;
        var scale = MathF.Min(width / bounds.Width, height / bounds.Height);
        canvas.Save();
        canvas.Translate(
            (width - bounds.Width * scale) * 0.5f - bounds.Left * scale,
            (height - bounds.Height * scale) * 0.5f - bounds.Top * scale);
        canvas.Scale(scale);
        canvas.DrawPicture(picture);
        canvas.Restore();
    }
}
