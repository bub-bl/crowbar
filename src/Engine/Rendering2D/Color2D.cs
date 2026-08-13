using System.Numerics;

namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// A straight (non-premultiplied) sRGB color in linear 0..1 floats — the
/// color space the 2D renderer's shaders consume. The fragment shader decodes
/// sRGB to linear on the GPU, so no CPU color conversion happens per frame.
/// </summary>
public readonly struct ColorF : IEquatable<ColorF>
{
    public readonly float R;
    public readonly float G;
    public readonly float B;
    public readonly float A;

    public ColorF(float r, float g, float b, float a = 1f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public static ColorF Transparent => new(0f, 0f, 0f, 0f);
    public static ColorF White => new(1f, 1f, 1f, 1f);
    public static ColorF Black => new(0f, 0f, 0f, 1f);

    /// <summary>Builds a color from 8-bit sRGB channels.</summary>
    public static ColorF FromRgba(byte r, byte g, byte b, byte a = 255) =>
        new(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>Returns this color with its alpha replaced by <paramref name="alpha"/>.</summary>
    public ColorF WithAlpha(float alpha) => new(R, G, B, alpha);

    /// <summary>Linear interpolation in sRGB space (matches <see cref="Crowbar.UI.UiColor.Lerp"/>).</summary>
    public static ColorF Lerp(ColorF from, ColorF to, float t) => new(
        from.R + (to.R - from.R) * t,
        from.G + (to.G - from.G) * t,
        from.B + (to.B - from.B) * t,
        from.A + (to.A - from.A) * t);

    public Vector4 ToVector4() => new(R, G, B, A);

    public bool Equals(ColorF other) => R.Equals(other.R) && G.Equals(other.G) && B.Equals(other.B) && A.Equals(other.A);
    public override bool Equals(object? obj) => obj is ColorF other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(R, G, B, A);
    public override string ToString() => $"rgba({R:0.###}, {G:0.###}, {B:0.###}, {A:0.###})";
}

/// <summary>
/// One gradient stop: an offset along the gradient (0..1) and its color.
/// Offsets are normalized ascending before upload.
/// </summary>
public readonly record struct GradientStop(float Offset, ColorF Color);

/// <summary>
/// A linear gradient along a start→end segment, carrying up to four stops
/// inline (the common UI case) so drawing a gradient allocates nothing. More
/// stops are truncated; the multi-stop extension will sample a gradient atlas
/// texture instead.
/// </summary>
public struct LinearGradient
{
    public Vector2 Start;
    public Vector2 End;
    public GradientStop Stop0;
    public GradientStop Stop1;
    public GradientStop Stop2;
    public GradientStop Stop3;
    public int StopCount;

    /// <summary>Copies up to four stops into the inline storage.</summary>
    public static LinearGradient Create(Vector2 start, Vector2 end, ReadOnlySpan<GradientStop> stops)
    {
        var gradient = new LinearGradient { Start = start, End = end };
        gradient.SetStops(stops);
        return gradient;
    }

    internal void SetStops(ReadOnlySpan<GradientStop> stops)
    {
        StopCount = Math.Min(4, stops.Length);
        if (stops.Length > 0) Stop0 = stops[0];
        if (stops.Length > 1) Stop1 = stops[1];
        if (stops.Length > 2) Stop2 = stops[2];
        if (stops.Length > 3) Stop3 = stops[3];
    }
}

/// <summary>
/// A radial gradient from a center with a radius, carrying up to four stops
/// inline (mirroring <see cref="LinearGradient"/>).
/// </summary>
public struct RadialGradient
{
    public Vector2 Center;
    public float Radius;
    public GradientStop Stop0;
    public GradientStop Stop1;
    public GradientStop Stop2;
    public GradientStop Stop3;
    public int StopCount;

    /// <summary>Copies up to four stops into the inline storage.</summary>
    public static RadialGradient Create(Vector2 center, float radius, ReadOnlySpan<GradientStop> stops)
    {
        var gradient = new RadialGradient { Center = center, Radius = MathF.Max(radius, 0.0001f) };
        gradient.SetStops(stops);
        return gradient;
    }

    internal void SetStops(ReadOnlySpan<GradientStop> stops)
    {
        StopCount = Math.Min(4, stops.Length);
        if (stops.Length > 0) Stop0 = stops[0];
        if (stops.Length > 1) Stop1 = stops[1];
        if (stops.Length > 2) Stop2 = stops[2];
        if (stops.Length > 3) Stop3 = stops[3];
    }
}
