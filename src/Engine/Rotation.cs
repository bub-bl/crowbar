using System.Numerics;

namespace Crowbar.Engine;

public record struct Rotation
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float W { get; set; }

    public Rotation()
    {
        X = 0f;
        Y = 0f;
        Z = 0f;
        W = 1f;
    }

    public Rotation(float x, float y, float z, float w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public static Rotation Identity => new(0f, 0f, 0f, 1f);
    public static Rotation Zero => Identity;
    public Quaternion Quaternion => new(X, Y, Z, W);

    public Rotation Normal => From(Quaternion.Normalize(Quaternion));
    public Rotation Conjugate => From(Quaternion.Conjugate(Quaternion));
    public Rotation Inverse => From(Quaternion.Inverse(Quaternion));

    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, Quaternion);
    public Vector3 Backward => Vector3.Transform(-Vector3.UnitZ, Quaternion);
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Quaternion);
    public Vector3 Down => Vector3.Transform(-Vector3.UnitY, Quaternion);
    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Quaternion);
    public Vector3 Left => Vector3.Transform(-Vector3.UnitX, Quaternion);

    public float Angle() => MathF.Abs(2f * MathF.Atan2(MathF.Sqrt(X * X + Y * Y + Z * Z), W)) * 180f / MathF.PI;
    public float Pitch() => Angles().Pitch;
    public float Yaw() => Angles().Yaw;
    public float Roll() => Angles().Roll;

    public Angles Angles()
    {
        var q = Normal.Quaternion;
        var sinPitch = 2f * (q.W * q.X + q.Y * q.Z);
        var cosPitch = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        var sinYaw = 2f * (q.W * q.Y - q.Z * q.X);
        var sinRoll = 2f * (q.W * q.Z + q.X * q.Y);
        var pitch = MathF.Atan2(sinPitch, cosPitch);
        var yaw = MathF.Abs(sinYaw) >= 1f ? MathF.CopySign(MathF.PI / 2f, sinYaw) : MathF.Asin(sinYaw);
        var roll = MathF.Atan2(sinRoll, 1f - 2f * (q.Y * q.Y + q.Z * q.Z));

        const float degrees = 180f / MathF.PI;
        return new Angles(pitch * degrees, yaw * degrees, roll * degrees);
    }

    public Rotation RotateAroundAxis(Vector3 axis, float degrees) => this * FromAxis(axis, degrees);
    public float Distance(Rotation to) => Difference(this, to).Angle();
    public bool AlmostEqual(Rotation other, float delta = 0.001f) => Distance(other) <= delta;
    public static Rotation From(Quaternion value) => new(value.X, value.Y, value.Z, value.W);
    public static Rotation From(Angles angles) => FromYaw(angles.Yaw) * FromPitch(angles.Pitch) * FromRoll(angles.Roll);

    public static Rotation FromAxis(Vector3 axis, float degrees)
    {
        return axis.LengthSquared() < float.Epsilon
            ? Identity
            : From(Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), degrees * MathF.PI / 180f));
    }

    public static Rotation FromPitch(float pitch) => FromAxis(Vector3.UnitX, pitch);
    public static Rotation FromYaw(float yaw) => FromAxis(Vector3.UnitY, yaw);
    public static Rotation FromRoll(float roll) => FromAxis(Vector3.UnitZ, roll);
    public static Rotation Difference(Rotation from, Rotation to) => from.Inverse * to;

    public static Rotation Lerp(Rotation a, Rotation b, float fraction, bool clamp = true)
    {
        if (clamp) fraction = Math.Clamp(fraction, 0f, 1f);
        return From(Quaternion.Lerp(a.Quaternion, b.Quaternion, fraction));
    }

    public static Rotation Slerp(Rotation a, Rotation b, float fraction, bool clamp = true)
    {
        if (clamp) fraction = Math.Clamp(fraction, 0f, 1f);
        return From(Quaternion.Slerp(a.Quaternion, b.Quaternion, fraction));
    }

    public Rotation LerpTo(Rotation target, float fraction, bool clamp = true) => Lerp(this, target, fraction, clamp);
    public Rotation SlerpTo(Rotation target, float fraction, bool clamp = true) => Slerp(this, target, fraction, clamp);

    public static Rotation Parse(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length != 4 || !float.TryParse(parts[0], out var x) || !float.TryParse(parts[1], out var y) ||
            !float.TryParse(parts[2], out var z) || !float.TryParse(parts[3], out var w))
            throw new FormatException("Rotation must use the format 'x,y,z,w'.");

        return new Rotation(x, y, z, w);
    }

    public static bool TryParse(string? value, out Rotation result)
    {
        if (value is not null)
        {
            try
            {
                result = Parse(value);
                return true;
            }
            catch (FormatException)
            {
            }
        }

        result = Identity;
        return false;
    }

    public static Rotation operator *(Rotation left, Rotation right) => From(left.Quaternion * right.Quaternion);

    public static Vector3 operator *(Rotation rotation, Vector3 vector) =>
        Vector3.Transform(vector, rotation.Quaternion);

    public override string ToString() => $"{X},{Y},{Z},{W}";
}