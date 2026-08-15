using System.Xml.Linq;
using Crowbar.Files;

namespace Crowbar.UI;

/// <summary>
/// Resolves the intrinsic size (the SVG's own coordinate space) of the icons
/// used by the <c>&lt;icon name="..."&gt;</c> panel. The source of truth is the
/// SVG pack in <c>Assets/Icons/&lt;name&gt;.svg</c> (the same convention as the
/// engine's <c>Assets/Gizmos</c>), so adding or swapping an icon is just a file
/// change. Only the root <c>viewBox</c> (or width/height) is read — the actual
/// drawing is done by the GPU renderer from the parsed vector paths, so this
/// cache carries no raster and no Skia dependency. A missing or malformed SVG
/// never throws — it simply reports no size and the panel sizes to its default.
/// </summary>
public sealed class SvgIconCache
{
    /// <summary>The process-wide cache used by the layout engine by default.</summary>
    public static SvgIconCache Shared { get; } = new();

    /// <summary>Directory icon names are resolved against (defaults to <c>Assets/Icons</c> in the app base directory).</summary>
    private string _contentRoot = PathUtil.Combine("Assets", "Icons");
    public string ContentRoot
    {
        get => _contentRoot;
        set
        {
            if (string.Equals(_contentRoot, value, StringComparison.Ordinal)) return;
            _contentRoot = value;
            Clear();
        }
    }

    private readonly Dictionary<string, (float Width, float Height)> _intrinsic = new(StringComparer.OrdinalIgnoreCase);
    // Resolving an icon requires a filesystem existence check. Cache both successful
    // and missing resolutions so the layout pass does not hit the filesystem on
    // every frame. Clear() invalidates this when assets are reloaded.
    private readonly Dictionary<string, string?> _resolved = new(StringComparer.OrdinalIgnoreCase);
    // The shared cache is used by every renderer in the process (tests run in
    // parallel, and hosts may load icons off the render thread); guard the
    // dictionaries so concurrent Get/Clear never corrupt them.
    private readonly object _lock = new();

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

        var size = ReadIntrinsicSize(path);
        if (size is not { } s || s.Width <= 0f || s.Height <= 0f) return false;
        width = s.Width;
        height = s.Height;
        lock (_lock) _intrinsic[path] = (width, height);
        return true;
    }

    /// <summary>Drops every cached measurement.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _intrinsic.Clear();
            _resolved.Clear();
        }
    }

    /// <summary>Reads the root <c>viewBox</c> (falling back to width/height attributes).</summary>
    private static (float Width, float Height)? ReadIntrinsicSize(string path)
    {
        try
        {
            var text = FileSystem.Content.ReadAllText(path);
            var root = XDocument.Parse(text).Root;
            if (root is null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                return null;

            var viewBox = root.Attribute("viewBox")?.Value;
            if (!string.IsNullOrWhiteSpace(viewBox))
            {
                var numbers = viewBox.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (numbers.Length >= 4 &&
                    float.TryParse(numbers[2], out var vbWidth) &&
                    float.TryParse(numbers[3], out var vbHeight))
                    return (vbWidth, vbHeight);
            }

            var width = ParseLength(root.Attribute("width")?.Value);
            var height = ParseLength(root.Attribute("height")?.Value);
            if (width is { } w && height is { } h)
                return (w, h);

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // A missing, locked or malformed icon must never crash the UI.
            return null;
        }
    }

    private static float? ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var number = value.Trim();
        var end = number.Length;
        while (end > 0 && !char.IsDigit(number[end - 1]) && number[end - 1] != '.')
            end--;
        return float.TryParse(number[..end], out var result) ? result : null;
    }

    // Subfolder paths are allowed (e.g. the Solar pack lives in
    // Assets/Icons/Solar/&lt;category&gt;/Bold/&lt;name&gt;.svg), so '/' is a valid name
    // char; every other file-name-invalid char (and '..' / leading '/') is
    // rejected so a name can never escape the icon directory.
    private static readonly char[] InvalidNameChars = PathUtil.InvalidFileNameChars
        .Where(c => c is not '/' and not '\\')
        .ToArray();

    private string? Resolve(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.IndexOfAny(InvalidNameChars) >= 0) return null;

        lock (_lock)
        {
            if (_resolved.TryGetValue(normalized, out var cached)) return cached;
            var path = PathUtil.Combine(_contentRoot, normalized + ".svg");
            var resolved = FileSystem.Content.FileExists(path) ? path : null;
            _resolved[normalized] = resolved;
            return resolved;
        }
    }
}
