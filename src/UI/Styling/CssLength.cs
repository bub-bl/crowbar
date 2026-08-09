namespace Crowbar.UI;

/// <summary>The unit of a <see cref="CssLength"/>.</summary>
public enum CssLengthUnit
{
    /// <summary>No value: the property keeps its default (Yoga resolves it).</summary>
    Undefined,
    /// <summary>Fixed pixel value (e.g. <c>12px</c> or a unitless number).</summary>
    Points,
    /// <summary>Percentage of the containing block, resolved by Yoga.</summary>
    Percent,
    /// <summary><c>auto</c>: content-based (sizes) or free (margins, offsets).</summary>
    Auto,
    /// <summary><c>max-content</c>: size to the content's intrinsic maximum.</summary>
    MaxContent,
    /// <summary><c>fit-content</c>: content size capped by the available space.</summary>
    FitContent,
}

/// <summary>
/// A CSS length usable by the styling engine. Keeping the unit attached to the
/// value lets Yoga.Net resolve percentages, auto margins and content-based
/// sizes natively instead of approximating them in the layout engine.
/// </summary>
public readonly struct CssLength : IEquatable<CssLength>
{
    public CssLengthUnit Unit { get; }
    public float Value { get; }

    private CssLength(CssLengthUnit unit, float value)
    {
        Unit = unit;
        Value = value;
    }

    public static CssLength Points(float value) => new(CssLengthUnit.Points, value);
    public static CssLength Percent(float value) => new(CssLengthUnit.Percent, value);

    public static readonly CssLength Auto = new(CssLengthUnit.Auto, 0);
    public static readonly CssLength MaxContent = new(CssLengthUnit.MaxContent, 0);
    public static readonly CssLength FitContent = new(CssLengthUnit.FitContent, 0);
    public static readonly CssLength Undefined = new(CssLengthUnit.Undefined, 0);

    /// <summary>True when the property carries an explicit value.</summary>
    public bool IsDefined => Unit != CssLengthUnit.Undefined;

    /// <summary>Resolved pixel value for point lengths, 0 otherwise.</summary>
    public float Px => Unit == CssLengthUnit.Points ? Value : 0;

    public bool Equals(CssLength other) => Unit == other.Unit && Value.Equals(other.Value);
    public override bool Equals(object? obj) => obj is CssLength other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Unit, Value);

    /// <summary>
    /// Interpolates two lengths, or null when they cannot be smoothly combined
    /// (different units, or keyword lengths such as <c>auto</c>). Callers fall
    /// back to the target value, mirroring CSS discrete animation.
    /// </summary>
    public static CssLength? Lerp(CssLength from, CssLength to, float t)
    {
        if (from.Unit != to.Unit) return null;
        if (from.Unit is not (CssLengthUnit.Points or CssLengthUnit.Percent)) return null;
        return new CssLength(from.Unit, from.Value + (to.Value - from.Value) * t);
    }

    public override string ToString() => Unit switch
    {
        CssLengthUnit.Points => $"{Value}px",
        CssLengthUnit.Percent => $"{Value}%",
        CssLengthUnit.Auto => "auto",
        CssLengthUnit.MaxContent => "max-content",
        CssLengthUnit.FitContent => "fit-content",
        _ => "undefined"
    };
}
