namespace Crowbar.UI;

/// <summary>
/// Registry of every CSS filter function the styling engine understands, plus
/// the list parser and the GPU-expressibility check. Registering a
/// <see cref="FilterFunctionDefinition"/> (see
/// <see cref="BuiltInFilterFunctions"/> for the standard set) is the extension
/// point: the function then flows through style-sheet parsing, cascading,
/// change detection and rendering without any further wiring.
/// </summary>
public static class CssFilterFunctions
{
    private static readonly Dictionary<string, FilterFunctionDefinition> Registry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every registered filter function, in registration order.</summary>
    public static IReadOnlyCollection<FilterFunctionDefinition> All => Registry.Values;

    public static bool TryGet(string name, out FilterFunctionDefinition definition) =>
        Registry.TryGetValue(name, out definition!);

    /// <summary>Registers a filter function. Throws when the name is already taken.</summary>
    public static void Register(FilterFunctionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!Registry.TryAdd(definition.Name, definition))
            throw new InvalidOperationException($"A CSS filter function named '{definition.Name}' is already registered.");
    }

    /// <summary>
    /// Parses a whole filter property value: <c>none</c> or an ordered list of
    /// functions such as <c>blur(4px) brightness(1.2)</c>. Mirroring CSS, any
    /// unknown or malformed function makes the whole value invalid.
    /// </summary>
    public static bool TryParse(string value, out CssFilter result)
    {
        result = CssFilter.None;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;

        var functions = new List<CssFilterFunction>();
        var i = 0;
        while (i < trimmed.Length)
        {
            while (i < trimmed.Length && char.IsWhiteSpace(trimmed[i])) i++;
            if (i >= trimmed.Length) break;

            var start = i;
            while (i < trimmed.Length && (char.IsAsciiLetterOrDigit(trimmed[i]) || trimmed[i] == '-')) i++;
            if (i >= trimmed.Length || trimmed[i] != '(') return false;
            var name = trimmed[start..i];

            // Read the balanced argument list, so colors such as rgb(0 0 0 / 50%)
            // and url() payloads survive the split.
            var argsStart = ++i;
            var depth = 1;
            while (i < trimmed.Length && depth > 0)
            {
                if (trimmed[i] == '(') depth++;
                else if (trimmed[i] == ')') depth--;
                i++;
            }
            if (depth != 0) return false;
            var arguments = trimmed[argsStart..(i - 1)].Trim();

            if (!TryParseFunction(name, arguments, out var function)) return false;
            functions.Add(function);
        }

        result = functions.Count == 0 ? CssFilter.None : new CssFilter(functions);
        return true;
    }

    /// <summary>Parses a single filter function from its name and raw arguments.</summary>
    public static bool TryParseFunction(string name, string arguments, out CssFilterFunction function)
    {
        function = null!;
        return Registry.TryGetValue(name, out var definition) && definition.TryParse(arguments, out function);
    }

    /// <summary>
    /// True when a filter chain can be evaluated by the GPU backdrop compositor
    /// (Backdrop.slang): at most one <c>blur</c> and only as the first function
    /// (a single-pass shader can blur the source texture, but not an
    /// intermediate result), plus any number of color transforms (brightness,
    /// contrast, saturate, grayscale, invert, hue-rotate, opacity, sepia).
    /// Chains the GPU cannot express (drop-shadow, blur after a color op, more
    /// than eight color ops) are dropped by the renderer.
    /// </summary>
    public static bool IsGpuBackdropExpressible(CssFilter filter)
    {
        if (filter.IsNone) return false;
        var sawBlur = false;
        var opCount = 0;
        foreach (var function in filter.Functions)
        {
            var isBlur = function.Name.Equals("blur", StringComparison.OrdinalIgnoreCase);
            if (isBlur)
            {
                // Blur is allowed once and only as the first function.
                if (sawBlur || opCount > 0) return false;
                sawBlur = true;
                continue;
            }
            if (++opCount > 8) return false;
            if (function.Name.Equals("drop-shadow", StringComparison.OrdinalIgnoreCase)) return false;
            if (function.Name.Equals("brightness", StringComparison.OrdinalIgnoreCase) ||
                function.Name.Equals("contrast", StringComparison.OrdinalIgnoreCase) ||
                function.Name.Equals("saturate", StringComparison.OrdinalIgnoreCase) ||
                function.Name.Equals("grayscale", StringComparison.OrdinalIgnoreCase) ||
                function.Name.Equals("invert", StringComparison.OrdinalIgnoreCase) ||
                function.Name.Equals("hue-rotate", StringComparison.OrdinalIgnoreCase) ||
                function.Name.Equals("opacity", StringComparison.OrdinalIgnoreCase) ||
                function.Name.Equals("sepia", StringComparison.OrdinalIgnoreCase))
                continue;
            return false;
        }
        return true;
    }

    static CssFilterFunctions() => BuiltInFilterFunctions.RegisterAll();
}
