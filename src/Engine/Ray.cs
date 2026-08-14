using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// A 3D ray (origin + normalized direction). Used by editor picking and the
/// translation gizmo; <see cref="FromScreen"/> unprojects a pixel position
/// through the camera like the grid shader does on the GPU.
/// </summary>
public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    public static Ray FromScreen(Vector2 screenPixels, int width, int height, Camera camera)
    {
        var aspect = Math.Max(1, width) / (float)Math.Max(1, height);
        return FromScreen(screenPixels, width, height, camera.ViewMatrix, camera.ProjectionMatrix(aspect));
    }

    public static Ray FromScreen(Vector2 screenPixels, int width, int height, Matrix4x4 view, Matrix4x4 projection)
    {
        // Screen pixels (origin top-left) to NDC (y up, -1..1), like the grid's unproject.
        var ndc = new Vector2(
            screenPixels.X / Math.Max(1, width) * 2f - 1f,
            1f - screenPixels.Y / Math.Max(1, height) * 2f);

        Matrix4x4.Invert(view * projection, out var inverseViewProjection);

        // Row-vector convention: clip = world * (view * projection), so
        // world = clip * Inverse(view * projection).
        var near = Vector4.Transform(new Vector4(ndc, 0f, 1f), inverseViewProjection);
        var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), inverseViewProjection);
        var nearWorld = new Vector3(near.X, near.Y, near.Z) / near.W;
        var farWorld = new Vector3(far.X, far.Y, far.Z) / far.W;

        return new Ray(nearWorld, Vector3.Normalize(farWorld - nearWorld));
    }

    /// <summary>
    /// The closest point on the infinite line <c>origin + t * axis</c> to this
    /// ray, and the parameter <c>t</c> along the axis. Returns false when the
    /// line and the ray are (nearly) parallel.
    /// </summary>
    public bool ClosestPointOnAxis(Vector3 axisOrigin, Vector3 axisDirection, out Vector3 point, out float t)
    {
        var direction = Vector3.Normalize(Direction);
        var w0 = Origin - axisOrigin;
        var a = Vector3.Dot(axisDirection, axisDirection);
        var b = Vector3.Dot(axisDirection, direction);
        var c = Vector3.Dot(direction, direction);
        var d = Vector3.Dot(axisDirection, w0);
        var e = Vector3.Dot(direction, w0);

        var denominator = a * c - b * b;
        if (MathF.Abs(denominator) < 1e-6f)
        {
            point = axisOrigin;
            t = 0f;
            return false;
        }

        // Min of |axis * t - ray * s - (rayOrigin - axisOrigin)|^2: the
        // stationarity equations give t = (c*d - b*e) / (a*c - b^2) with
        // w0 = rayOrigin - axisOrigin.
        t = (c * d - b * e) / denominator;
        point = axisOrigin + axisDirection * t;
        return true;
    }

    /// <summary>
    /// The point where this ray crosses the plane through <paramref name="planePoint"/>
    /// with the given <paramref name="planeNormal"/>, or false when the ray runs
    /// (nearly) parallel to the plane.
    /// </summary>
    public bool IntersectsPlane(Vector3 planePoint, Vector3 planeNormal, out Vector3 point)
    {
        var denominator = Vector3.Dot(Direction, planeNormal);
        if (MathF.Abs(denominator) < 1e-6f)
        {
            point = default;
            return false;
        }

        var t = Vector3.Dot(planePoint - Origin, planeNormal) / denominator;
        point = Origin + Direction * t;
        return true;
    }

    /// <summary>
    /// Slab-based AABB intersection. Returns the entry distance along the ray
    /// (negative when the origin is inside the box).
    /// </summary>
    public bool Intersects(in Bounds bounds, out float distance)
    {
        var direction = Direction;
        var tMin = 0f;
        var tMax = float.MaxValue;

        if (!Slab(bounds.Min.X, bounds.Max.X, Origin.X, direction.X, ref tMin, ref tMax) ||
            !Slab(bounds.Min.Y, bounds.Max.Y, Origin.Y, direction.Y, ref tMin, ref tMax) ||
            !Slab(bounds.Min.Z, bounds.Max.Z, Origin.Z, direction.Z, ref tMin, ref tMax))
        {
            distance = 0f;
            return false;
        }

        distance = tMin;
        return true;
    }

    private static bool Slab(float min, float max, float origin, float direction, ref float tMin, ref float tMax)
    {
        if (MathF.Abs(direction) < 1e-12f)
            return origin >= min && origin <= max;

        var inverse = 1f / direction;
        var t1 = (min - origin) * inverse;
        var t2 = (max - origin) * inverse;
        if (t1 > t2)
            (t1, t2) = (t2, t1);

        tMin = MathF.Max(tMin, t1);
        tMax = MathF.Min(tMax, t2);
        return tMin <= tMax;
    }
}
