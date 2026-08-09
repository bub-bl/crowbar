using SkiaSharp;

namespace Crowbar.UI;

/// <summary>
/// A single CSS filter function understood by the styling engine. This is the
/// extension point of the whole filter pipeline: subclass it, implement
/// <see cref="TryParse"/> (CSS syntax) and <see cref="CreateImageFilter"/>
/// (Skia effect), then call <see cref="CssFilterFunctions.Register"/>. The
/// function then participates in style-sheet parsing, cascading, change
/// detection and rendering without any further wiring.
/// </summary>
public abstract class FilterFunctionDefinition
{
    protected FilterFunctionDefinition(string name) => Name = name;

    /// <summary>The CSS function name, e.g. <c>blur</c>.</summary>
    public string Name { get; }

    /// <summary>
    /// Parses the function arguments (the text inside the parentheses) into a
    /// typed <see cref="CssFilterFunction"/>. Returns false when the arguments
    /// are invalid, which invalidates the whole filter list (CSS semantics).
    /// </summary>
    public abstract bool TryParse(string arguments, out CssFilterFunction function);

    /// <summary>
    /// Builds the Skia image filter for this function. <paramref name="input"/>
    /// is the already-built inner part of the chain, or null for the innermost
    /// function; the returned filter must apply this function to it.
    /// </summary>
    public abstract SKImageFilter? CreateImageFilter(CssFilterFunction function, SKImageFilter? input);
}
