using Crowbar.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Crowbar.UI;

/// <summary>
/// Resolves the intrinsic size of the images referenced by the UI pipeline:
/// the <c>src</c> of an <c>&lt;img&gt;</c> panel and the <c>url(...)</c> of
/// <c>background-image</c>. A source is resolved against
/// <see cref="ContentRoot"/> when it is a relative path (absolute paths are
/// used as-is, so SCSS/CSS can point anywhere on disk); <c>data:</c> URIs are
/// decoded inline. Only the decoded dimensions are cached — the actual pixels
/// are loaded by the GPU renderer's image atlas, so this cache carries no
/// Skia dependency. Entries are cached by normalized source so the per-frame
/// layout measure never decodes twice.
/// </summary>
public sealed class UiImageCache
{
    /// <summary>The process-wide cache used by the layout engine by default.</summary>
    public static UiImageCache Shared { get; } = new();

    /// <summary>Directory relative image sources are resolved against (defaults to the app base directory).</summary>
    public string ContentRoot { get; set; } = AppContext.BaseDirectory;

    private readonly Dictionary<string, (int Width, int Height)> _sizes = new(StringComparer.OrdinalIgnoreCase);
    // The shared cache is used by every renderer in the process (tests run in
    // parallel, and hosts may load images off the render thread); guard the
    // dictionary so concurrent Register/Get/Clear never corrupt it.
    private readonly object _lock = new();

    /// <summary>
    /// Registers an intrinsic size under a source key without touching the
    /// disk. This is the injection point for tests and for hosts that resolve
    /// image dimensions through their own asset pipeline.
    /// </summary>
    public void Register(string source, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (width < 0 || height < 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be non-negative.");
        lock (_lock) _sizes[source] = (width, height);
    }

    /// <summary>
    /// Gets the intrinsic size of a source, or false when it is unknown. Used
    /// by the layout engine to size <c>&lt;img&gt;</c> panels and to resolve
    /// <c>aspect-ratio: auto</c>.
    /// </summary>
    public bool TryGetSize(string source, out float width, out float height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(source)) return false;

        lock (_lock)
        {
            if (_sizes.TryGetValue(source, out var cached))
            {
                width = cached.Width;
                height = cached.Height;
                return cached.Width > 0 && cached.Height > 0;
            }
        }

        var size = ReadSize(source);
        if (size is not { } s || s.Width <= 0 || s.Height <= 0) return false;
        lock (_lock) _sizes[source] = s;
        width = s.Width;
        height = s.Height;
        return true;
    }

    /// <summary>Drops every cached size.</summary>
    public void Clear()
    {
        lock (_lock) _sizes.Clear();
    }

    private (int Width, int Height)? ReadSize(string source)
    {
        try
        {
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = source.IndexOf(',');
                if (comma < 0) return null;
                var header = source[..comma];
                if (!header.Contains("base64", StringComparison.OrdinalIgnoreCase)) return null;
                var payload = source[(comma + 1)..].Trim();
                // Strip any data-URI quoting the CSS url() may have kept.
                if (payload.Length >= 2 && payload[0] is '"' or '\'' && payload[^1] == payload[0])
                    payload = payload[1..^1];
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(payload);
                }
                catch (FormatException)
                {
                    return null;
                }
                using var stream = new MemoryStream(bytes);
                using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(stream);
                return (image.Width, image.Height);
            }

            var fs = FileSystem.Content;
            var path = fs.ToFilePath(FileSystemService.IsRooted(source) ? source : PathUtil.Combine(ContentRoot, source));
            if (!fs.FileExists(path)) return null;
            using var imageStream = fs.OpenRead(path);
            using var loaded = SixLabors.ImageSharp.Image.Load<Rgba32>(imageStream);
            return (loaded.Width, loaded.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A missing, locked or undecodable image must never crash the UI.
            return null;
        }
    }
}
