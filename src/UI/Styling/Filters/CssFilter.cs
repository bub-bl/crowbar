namespace Crowbar.UI;

/// <summary>
/// A single parsed CSS filter function, e.g. <c>blur(4px)</c> or
/// <c>drop-shadow(2px 2px 4px #000000)</c>. The function carries its numeric
/// arguments and (for <c>drop-shadow</c>) an optional color; how those are
/// interpreted is owned by the matching <see cref="FilterFunctionDefinition"/>.
/// </summary>
public sealed class CssFilterFunction : IEquatable<CssFilterFunction>
{
    /// <summary>The function name (blur, brightness, drop-shadow, ...).</summary>
    public string Name { get; }

    /// <summary>Numeric arguments in declaration order (amounts, radii, offsets, angles).</summary>
    public IReadOnlyList<float> Parameters { get; }

    /// <summary>Optional color argument, used by <c>drop-shadow</c>.</summary>
    public UiColor? Color { get; }

    internal CssFilterFunction(string name, IReadOnlyList<float> parameters, UiColor? color = null)
    {
        Name = name;
        Parameters = parameters;
        Color = color;
    }

    public bool Equals(CssFilterFunction? other) =>
        other is not null &&
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) &&
        Color == other.Color &&
        Parameters.Count == other.Parameters.Count &&
        Parameters.SequenceEqual(other.Parameters);

    public override bool Equals(object? obj) => obj is CssFilterFunction other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name, StringComparer.OrdinalIgnoreCase);
        hash.Add(Color);
        foreach (var parameter in Parameters) hash.Add(parameter);
        return hash.ToHashCode();
    }
}

/// <summary>
/// An ordered list of CSS filter functions applied in declaration order
/// (<c>blur(4px) brightness(1.2)</c>). Structural equality makes the value
/// participate in the style engine's change detection, so editing a filter in
/// CSS re-renders exactly when the list actually changes.
/// </summary>
public sealed class CssFilter : IEquatable<CssFilter>
{
    /// <summary>The <c>none</c> filter: no effect.</summary>
    public static readonly CssFilter None = new([]);

    public IReadOnlyList<CssFilterFunction> Functions { get; }

    /// <summary>True when the list is empty (<c>none</c>).</summary>
    public bool IsNone => Functions.Count == 0;

    internal CssFilter(IReadOnlyList<CssFilterFunction> functions) => Functions = functions;

    public bool Equals(CssFilter? other) =>
        other is not null &&
        Functions.Count == other.Functions.Count &&
        Functions.Zip(other.Functions).All(pair => pair.First.Equals(pair.Second));

    public override bool Equals(object? obj) => obj is CssFilter other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var function in Functions) hash.Add(function);
        return hash.ToHashCode();
    }

    public override string ToString() => IsNone ? "none" : string.Join(" ", Functions.Select(Format));

    private static string Format(CssFilterFunction function)
    {
        var args = string.Join(" ", function.Parameters.Select(p => p.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
        return function.Color is { } color && function.Name.Equals("drop-shadow", StringComparison.OrdinalIgnoreCase)
            ? $"{function.Name}({args} {color})"
            : $"{function.Name}({args})";
    }
}
