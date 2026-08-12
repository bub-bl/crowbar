using SkiaSharp;

namespace Crowbar.UI;

/// <summary>
/// Loads and caches the images referenced by the UI pipeline: the <c>src</c>
/// of an <c>&lt;img&gt;</c> panel and the <c>url(...)</c> of
/// <c>background-image</c>. A source is resolved against
/// <see cref="ContentRoot"/> when it is a relative path (absolute paths are
/// used as-is, so SCSS/CSS can point anywhere on disk); <c>data:</c> URIs are
/// decoded inline. Entries are cached by normalized source so the per-frame
/// paint pass and the layout measure never decode twice.
/// </summary>
public sealed class UiImageCache
{
    /// <summary>The process-wide cache used by the renderer and the layout engine by default.</summary>
    public static UiImageCache Shared { get; } = new();

    /// <summary>Directory relative image sources are resolved against (defaults to the app base directory).</summary>
    public string ContentRoot { get; set; } = AppContext.BaseDirectory;

    private readonly Dictionary<string, SKImage> _images = new(StringComparer.OrdinalIgnoreCase);
    // The shared cache is used by every renderer in the process (tests render
    // in parallel, and hosts may load images off the render thread); guard the
    // dictionary so concurrent Register/Get/Clear never corrupt it.
    private readonly object _lock = new();

    /// <summary>
    /// Registers an image under a source key without touching the disk. This is
    /// the injection point for tests and for hosts that decode images through
    /// their own asset pipeline (the cache takes ownership of the image and
    /// disposes it on <see cref="Clear"/> or on re-registration).
    /// </summary>
    public void Register(string source, SKImage image)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(image);
        lock (_lock)
        {
            if (_images.TryGetValue(source, out var previous)) previous.Dispose();
            _images[source] = image;
        }
    }

    /// <summary>Gets the decoded image for a source, or null when it cannot be loaded.</summary>
    public SKImage? Get(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        lock (_lock)
        {
            if (_images.TryGetValue(source, out var cached)) return cached;
        }
        var decoded = Decode(source);
        if (decoded is not null)
        {
            lock (_lock) _images[source] = decoded;
        }
        return decoded;
    }

    /// <summary>
    /// Gets the intrinsic size of a source, or false when it is unknown. Used
    /// by the layout engine to size <c>&lt;img&gt;</c> panels and to resolve
    /// <c>aspect-ratio: auto</c>.
    /// </summary>
    public bool TryGetSize(string source, out float width, out float height)
    {
        if (Get(source) is { } image)
        {
            width = image.Width;
            height = image.Height;
            return true;
        }
        width = 0;
        height = 0;
        return false;
    }

    /// <summary>Drops every cached image (disposing the decoded bitmaps).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var image in _images.Values) image.Dispose();
            _images.Clear();
        }
    }

    private SKImage? Decode(string source)
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
                try
                {
                    return SKImage.FromEncodedData(Convert.FromBase64String(payload));
                }
                catch (FormatException)
                {
                    return null;
                }
            }

            var path = Path.IsPathRooted(source) ? source : Path.Combine(ContentRoot, source);
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return SKImage.FromEncodedData(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A missing, locked or undecodable image must never crash the UI:
            // the paint pass simply draws nothing for that panel.
            return null;
        }
    }
}
