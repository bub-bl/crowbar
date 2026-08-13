namespace Crowbar.Engine.Rendering2D;

/// <summary>The CSS filter operations the GPU filter pass understands.</summary>
public enum FilterOpKind : byte
{
    Blur = 0,
    Brightness = 1,
    Contrast = 2,
    Grayscale = 3,
    HueRotate = 4,
    Invert = 5,
    Opacity = 6,
    Saturate = 7,
    Sepia = 8
}

/// <summary>One filter operation: a kind and its amount (radius, multiplier, angle, ...).</summary>
public readonly record struct FilterOp(FilterOpKind Kind, float Amount)
{
    public static FilterOp Blur(float radius) => new(FilterOpKind.Blur, radius);
    public static FilterOp Brightness(float amount) => new(FilterOpKind.Brightness, amount);
    public static FilterOp Contrast(float amount) => new(FilterOpKind.Contrast, amount);
    public static FilterOp Grayscale(float amount) => new(FilterOpKind.Grayscale, amount);
    public static FilterOp HueRotate(float degrees) => new(FilterOpKind.HueRotate, degrees);
    public static FilterOp Invert(float amount) => new(FilterOpKind.Invert, amount);
    public static FilterOp Opacity(float amount) => new(FilterOpKind.Opacity, amount);
    public static FilterOp Saturate(float amount) => new(FilterOpKind.Saturate, amount);
    public static FilterOp Sepia(float amount) => new(FilterOpKind.Sepia, amount);
}

/// <summary>
/// An ordered, composable list of <see cref="FilterOp"/>s applied to a drawing
/// subtree between <see cref="Renderer2D.PushFilter"/> and
/// <see cref="Renderer2D.PopFilter"/>. The renderer renders the subtree to an
/// offscreen target and composites it back through the filter pass, in
/// declaration order. This is the engine-native filter type; the UI maps its
/// <c>CssFilter</c> onto it so the renderer stays framework-independent.
/// </summary>
public sealed class Filter2D : IEquatable<Filter2D>
{
    /// <summary>The empty filter (no effect).</summary>
    public static readonly Filter2D None = new([]);

    /// <summary>The operations, in application order.</summary>
    public IReadOnlyList<FilterOp> Ops { get; }

    /// <summary>True when the list is empty.</summary>
    public bool IsNone => Ops.Count == 0;

    private Filter2D(IReadOnlyList<FilterOp> ops) => Ops = ops;

    /// <summary>Builds a filter from an ordered list of operations.</summary>
    public static Filter2D Create(params FilterOp[] ops) => ops.Length == 0 ? None : new Filter2D(ops);

    /// <summary>Builds a filter from an ordered list of operations.</summary>
    public static Filter2D Create(IReadOnlyList<FilterOp> ops) => ops.Count == 0 ? None : new Filter2D(ops);

    public bool Equals(Filter2D? other) =>
        other is not null &&
        Ops.Count == other.Ops.Count &&
        Ops.Zip(other.Ops).All(pair => pair.First.Equals(pair.Second));

    public override bool Equals(object? obj) => obj is Filter2D other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var op in Ops)
            hash.Add(op);
        return hash.ToHashCode();
    }

    public override string ToString() => IsNone ? "none" : string.Join(" ", Ops);
}
