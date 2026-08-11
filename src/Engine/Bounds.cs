using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// An axis-aligned bounding box (model or world space). Used by editor
/// picking; <see cref="TransformBy"/> converts a model-space box to world
/// space through the eight corner points.
/// </summary>
public readonly record struct Bounds(Vector3 Min, Vector3 Max)
{
    public static readonly Bounds Empty = new(Vector3.Zero, Vector3.Zero);

    public static Bounds FromPoints(IEnumerable<Vector3> points)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;
        foreach (var point in points)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
            any = true;
        }

        return any ? new Bounds(min, max) : Empty;
    }

    public Bounds TransformBy(in Matrix4x4 matrix)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? Min.X : Max.X,
                (i & 2) == 0 ? Min.Y : Max.Y,
                (i & 4) == 0 ? Min.Z : Max.Z);
            var transformed = Vector3.Transform(corner, matrix);
            min = Vector3.Min(min, transformed);
            max = Vector3.Max(max, transformed);
        }

        return new Bounds(min, max);
    }
}
