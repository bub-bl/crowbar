using System.Numerics;

namespace Crowbar.Engine;

/// <summary>RGBA color represented by normalized floating-point channels.</summary>
public readonly record struct Color(float R, float G, float B, float A = 1f)
{
    public static Color White => new(1f, 1f, 1f, 1f);
    public static Color Black => new(0f, 0f, 0f, 1f);
    public static Color Transparent => new(0f, 0f, 0f, 0f);

    public Vector4 ToVector4() => new(R, G, B, A);
    public static Color FromVector4(Vector4 value) => new(value.X, value.Y, value.Z, value.W);
    public Color WithAlpha(float alpha) => this with { A = alpha };
}
