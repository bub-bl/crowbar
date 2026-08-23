using System.Numerics;

namespace Crowbar.Engine;

public static class EnvironmentMath
{
    public static float RadicalInverse(uint bits)
    {
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        return bits * 2.3283064365386963e-10f;
    }

    public static Vector2 Hammersley(uint index, uint count)
    {
        if (count == 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        return new Vector2(index / (float)count, RadicalInverse(index));
    }

    public static float RoughnessToMip(float roughness, int mipCount) =>
        Math.Clamp(roughness, 0f, 1f) * Math.Max(0, mipCount - 1);

    public static Vector3 RotateY(Vector3 direction, float radians)
    {
        var sine = MathF.Sin(radians);
        var cosine = MathF.Cos(radians);
        return new Vector3(
            cosine * direction.X - sine * direction.Z,
            direction.Y,
            sine * direction.X + cosine * direction.Z);
    }
}
