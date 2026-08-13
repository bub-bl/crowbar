namespace Crowbar.Engine.Rendering2D;

/// <summary>Horizontal alignment of text within its wrapping box.</summary>
public enum TextAlign
{
    Left,
    Center,
    Right
}

/// <summary>
/// The typography contract for <see cref="Renderer2D.DrawText"/>: size, color,
/// family/weight, letter spacing, wrapping width, line height and alignment.
/// Font sizes are in pixels (points at 72 DPI), matching the Skia convention
/// the UI layout engine already uses.
/// </summary>
public readonly struct TextStyle
{
    public readonly float FontSize;
    public readonly ColorF Color;
    public readonly string Family;
    public readonly int Weight;
    public readonly float LetterSpacing;
    public readonly float MaxWidth;
    public readonly float LineHeight;
    public readonly TextAlign Align;

    public TextStyle(
        float fontSize,
        ColorF color,
        string family = "",
        int weight = 400,
        float letterSpacing = 0f,
        float maxWidth = 0f,
        float lineHeight = 0f,
        TextAlign align = TextAlign.Left)
    {
        FontSize = fontSize;
        Color = color;
        Family = family;
        Weight = weight;
        LetterSpacing = letterSpacing;
        MaxWidth = maxWidth;
        LineHeight = lineHeight;
        Align = align;
    }
}
