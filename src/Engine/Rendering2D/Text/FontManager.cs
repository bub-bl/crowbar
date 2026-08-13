using SixLabors.Fonts;

namespace Crowbar.Engine.Rendering2D;

/// <summary>A resolved font family + style with a stable cache key.</summary>
internal readonly record struct ResolvedFont(FontFamily Family, FontStyle Style, string Key)
{
    public Font CreateFont(float size) => Family.CreateFont(size, Style);
}

/// <summary>
/// Resolves (family name, CSS weight) pairs to a concrete <see cref="FontFamily"/>
/// using the system font set, with a deterministic fallback chain so text
/// renders even when a family is missing. Results are cached.
/// </summary>
internal sealed class FontManager
{
    private readonly Dictionary<(string Family, int Weight), ResolvedFont> _cache = [];

    private static readonly string[] FallbackFamilies =
        ["Segoe UI", "Arial", "DejaVu Sans", "Liberation Sans", "Helvetica", "Roboto"];

    /// <summary>Resolves a family/weight pair (cached).</summary>
    public ResolvedFont Resolve(string family, int weight)
    {
        var key = (family, weight);
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        var resolved = ResolveFamily(family, weight);
        _cache[key] = resolved;
        return resolved;
    }

    private static ResolvedFont ResolveFamily(string family, int weight)
    {
        var style = weight >= 600 ? FontStyle.Bold : FontStyle.Regular;

        if (!string.IsNullOrWhiteSpace(family) && SystemFonts.TryGet(family, out var requested))
            return new ResolvedFont(requested, style, $"{requested.Name}|{style}");

        foreach (var name in FallbackFamilies)
        {
            if (SystemFonts.TryGet(name, out var fallback))
                return new ResolvedFont(fallback, style, $"{fallback.Name}|{style}");
        }

        foreach (var available in SystemFonts.Families)
            return new ResolvedFont(available, style, $"{available.Name}|{style}");

        throw new InvalidOperationException("No system fonts are available for text rendering.");
    }
}
