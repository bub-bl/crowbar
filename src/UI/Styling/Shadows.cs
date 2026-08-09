namespace Crowbar.UI;

/// <summary>
/// A single CSS <c>box-shadow</c>: an offset, optional blur and spread, a color
/// and the <c>inset</c> flag. Offsets and spread may be negative; blur is
/// clamped to zero. Multiple shadows paint in declaration order (first on top).
/// </summary>
public readonly record struct BoxShadow(
    float OffsetX,
    float OffsetY,
    float BlurRadius,
    float SpreadRadius,
    UiColor Color,
    bool Inset)
{
    /// <summary>Interpolates two shadows; the <c>inset</c> flag flips discretely at the midpoint.</summary>
    public static BoxShadow Lerp(BoxShadow from, BoxShadow to, float t) => new(
        from.OffsetX + (to.OffsetX - from.OffsetX) * t,
        from.OffsetY + (to.OffsetY - from.OffsetY) * t,
        from.BlurRadius + (to.BlurRadius - from.BlurRadius) * t,
        from.SpreadRadius + (to.SpreadRadius - from.SpreadRadius) * t,
        UiColor.Lerp(from.Color, to.Color, t),
        t >= 0.5f ? to.Inset : from.Inset);
}

/// <summary>
/// A single CSS <c>text-shadow</c>: an offset, an optional blur radius and a
/// color. Inherited like <c>color</c>. Multiple shadows paint in declaration
/// order (first on top).
/// </summary>
public readonly record struct TextShadow(
    float OffsetX,
    float OffsetY,
    float BlurRadius,
    UiColor Color)
{
    /// <summary>Interpolates two text shadows.</summary>
    public static TextShadow Lerp(TextShadow from, TextShadow to, float t) => new(
        from.OffsetX + (to.OffsetX - from.OffsetX) * t,
        from.OffsetY + (to.OffsetY - from.OffsetY) * t,
        from.BlurRadius + (to.BlurRadius - from.BlurRadius) * t,
        UiColor.Lerp(from.Color, to.Color, t));
}
