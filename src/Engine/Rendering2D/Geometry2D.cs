using System.Numerics;

namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// An axis-aligned rectangle in local drawing coordinates. All 2D renderer
/// geometry is expressed in this space; the transform stack maps it to the
/// target's pixel space.
/// </summary>
public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public Vector2 TopLeft => new(X, Y);
    public Vector2 BottomRight => new(Right, Bottom);
    public Vector2 Center => new(X + Width * 0.5f, Y + Height * 0.5f);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectF FromLTRB(float left, float top, float right, float bottom) =>
        new(left, top, right - left, bottom - top);

    public RectF Offset(Vector2 delta) => new(X + delta.X, Y + delta.Y, Width, Height);

    public RectF Inflate(float amount) => new(X - amount, Y - amount, Width + amount * 2, Height + amount * 2);

    public bool Contains(Vector2 point) =>
        point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;

    /// <summary>Intersection with another rect, or an empty rect when they do not overlap.</summary>
    public RectF Intersect(RectF other)
    {
        var left = MathF.Max(X, other.X);
        var top = MathF.Max(Y, other.Y);
        var right = MathF.Min(Right, other.Right);
        var bottom = MathF.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? default : FromLTRB(left, top, right, bottom);
    }
}

/// <summary>
/// The style of a border stroke. <see cref="Solid"/> is a plain ring;
/// <see cref="Dashed"/> and <see cref="Dotted"/> modulate the stroke along the
/// shape's outline (dotted uses round dots), and <see cref="Double"/> draws two
/// concentric rings. Only meaningful when a stroke width is supplied.
/// </summary>
public enum BorderStyle : byte
{
    Solid = 0,
    Dashed = 1,
    Dotted = 2,
    Double = 3
}
