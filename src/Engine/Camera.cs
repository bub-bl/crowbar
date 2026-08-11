using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// Runtime camera state (position + yaw/pitch look direction). It is a plain
/// data holder: input controllers (the application) move it, and the graphics
/// device derives view/projection matrices from it when rendering. Keeping it
/// out of the renderer lets any input scheme drive the scene.
/// </summary>
public sealed class Camera
{
    public Vector3 Position { get; set; } = new(4.24f, 3f, 4.24f);
    public float Yaw { get; set; } = -MathF.PI / 4f;
    public float Pitch { get; set; } = -0.42f;
    public float FieldOfView { get; set; } = MathF.PI / 3f;
    public float NearPlane { get; set; } = 0.1f;
    public float FarPlane { get; set; } = 100f;

    public Vector3 Forward => new(
        MathF.Sin(Yaw) * MathF.Cos(Pitch),
        MathF.Sin(Pitch),
        -MathF.Cos(Yaw) * MathF.Cos(Pitch));

    public Vector3 Right => new(MathF.Cos(Yaw), 0f, MathF.Sin(Yaw));

    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);

    public Matrix4x4 ProjectionMatrix(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect, NearPlane, FarPlane);
}
