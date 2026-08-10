namespace Crowbar.UI;

/// <summary>How a background image is fitted into its area (<c>background-size</c>).</summary>
public enum BackgroundSizeType
{
    /// <summary><c>auto</c>: the image's intrinsic size (one auto axis scales to keep the ratio).</summary>
    Auto,
    /// <summary><c>cover</c>: scale to cover the whole area, cropping the overflow.</summary>
    Cover,
    /// <summary><c>contain</c>: scale to fit inside the area, letterboxing the rest.</summary>
    Contain,
    /// <summary>One or two explicit <see cref="CssLength"/> values (pixels or percentages).</summary>
    Explicit
}

/// <summary>The resolved <c>background-size</c> value.</summary>
public readonly record struct BackgroundSize(BackgroundSizeType Type, CssLength Width, CssLength Height)
{
    /// <summary><c>auto auto</c> (the initial value).</summary>
    public static readonly BackgroundSize AutoAuto = new(BackgroundSizeType.Auto, CssLength.Undefined, CssLength.Undefined);
}

/// <summary>How a background tile is repeated along one axis (<c>background-repeat</c>).</summary>
public enum RepeatMode
{
    Repeat,
    NoRepeat,
    Space,
    Round
}

/// <summary>The per-axis repeat mode of a background image.</summary>
public readonly record struct CssRepeat(RepeatMode X, RepeatMode Y)
{
    /// <summary><c>repeat</c> on both axes (the initial value).</summary>
    public static readonly CssRepeat Repeat = new(RepeatMode.Repeat, RepeatMode.Repeat);
}

/// <summary>
/// A CSS position (<c>background-position</c>, <c>object-position</c>): a
/// horizontal and a vertical component, each a percentage, a length or
/// <c>auto</c>. Keywords are normalized to percentages (<c>left</c> = 0%,
/// <c>center</c> = 50%, <c>right</c> = 100%, ...) so a single resolution rule
/// applies at paint time.
/// </summary>
public readonly record struct CssPosition(CssLength X, CssLength Y)
{
    /// <summary><c>0% 0%</c> — the initial <c>background-position</c>.</summary>
    public static readonly CssPosition TopLeft = new(CssLength.Percent(0), CssLength.Percent(0));
    /// <summary><c>50% 50%</c> — the initial <c>object-position</c>.</summary>
    public static readonly CssPosition Center = new(CssLength.Percent(50), CssLength.Percent(50));
}
