using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// Runtime camera: a spatial component (the analog of Unreal's
/// UCameraComponent) holding the view state of the scene. Its position comes
/// from the transform hierarchy, so attaching it to an entity follows that
/// entity. The look direction is edited through <see cref="Yaw"/> and
/// <see cref="Pitch"/> — the free/orbit camera convention — and mirrored into
/// <see cref="TransformComponent.Local"/>'s rotation; input controllers drive
/// it and the graphics device derives view/projection matrices from it when
/// rendering.
/// </summary>
[GizmoIcon("camera")]
public sealed class Camera : TransformComponent
{
    private const float RadiansToDegrees = 180f / MathF.PI;

    private float _yaw = -MathF.PI / 4f;
    private float _pitch = -0.42f;

    public Camera()
    {
        var position = new Vector3(4.24f, 3f, -4.24f);
        var rotation = RotationFromYawPitch(_yaw, _pitch);
        // Métadonnées d'orbite cohérentes avec la vue de départ : le pivot est
        // le point que la caméra regarde, à la distance initiale. Le contrôleur
        // maintient ensuite Position == Pivot - Forward * Distance.
        Distance = position.Length();
        Pivot = position + rotation.Forward * Distance;
        Local = new Transform(position, rotation, Vector3.One);
    }

    /// <summary>
    /// World-space position. When the camera is attached to an entity this
    /// follows the transform hierarchy, exactly like any other spatial
    /// component.
    /// </summary>
    public Vector3 Position
    {
        get => World.Position;
        set => World = World with { Position = value };
    }

    /// <summary>
    /// Point du monde autour duquel la caméra orbite (espace monde), piloté
    /// par le contrôleur de caméra. Avec <see cref="Distance"/>, il définit la
    /// sphère d'orbite : le contrôleur maintient
    /// <c>Position == Pivot - Forward * Distance</c> après chaque contrôle.
    /// </summary>
    public Vector3 Pivot { get; set; }

    /// <summary>Distance entre la caméra et <see cref="Pivot"/> (rayon d'orbite).</summary>
    public float Distance { get; set; }

    /// <summary>Look yaw in radians (free-camera convention).</summary>
    public float Yaw
    {
        get => _yaw;
        set
        {
            _yaw = value;
            SyncRotation();
        }
    }

    /// <summary>Look pitch in radians (free-camera convention).</summary>
    public float Pitch
    {
        get => _pitch;
        set
        {
            _pitch = value;
            SyncRotation();
        }
    }

    public float FieldOfView { get; set; } = MathF.PI / 3f;

    public float NearPlane { get; set; } = 0.1f;

    public float FarPlane { get; set; } = 100f;

    /// <summary>World-space look direction (the transform's forward).</summary>
    public Vector3 Forward => Local.Rotation.Forward;

    /// <summary>World-space right direction (the transform's right).</summary>
    public Vector3 Right => Local.Rotation.Right;

    /// <summary>World-space up direction (the transform's up).</summary>
    public Vector3 Up => Local.Rotation.Up;

    public Matrix4x4 ViewMatrix
    {
        get
        {
            // Left-handed view (Unity/DirectX): the camera looks down +Z, with
            // +X on the right of the screen, +Y up and +Z into it. Built by
            // hand (System.Numerics' CreateLookAt is right-handed); row-vector
            // layout, so view = world * ViewMatrix like every other matrix here.
            var eye = Position;
            var forward = Forward;
            var up = Up;
            var right = Vector3.Normalize(Vector3.Cross(up, forward));
            var upVector = Vector3.Cross(forward, right);
            return new Matrix4x4(
                right.X, upVector.X, forward.X, 0f,
                right.Y, upVector.Y, forward.Y, 0f,
                right.Z, upVector.Z, forward.Z, 0f,
                -Vector3.Dot(right, eye), -Vector3.Dot(upVector, eye), -Vector3.Dot(forward, eye), 1f);
        }
    }

    public Matrix4x4 ProjectionMatrix(float aspect)
    {
        // Left-handed perspective (Unity/DirectX): view +Z (forward) maps to
        // NDC z in [0, 1], WebGPU's depth range. Row-vector layout.
        var yScale = 1f / MathF.Tan(FieldOfView * 0.5f);
        var xScale = yScale / Math.Max(1e-6f, aspect);
        var zScale = FarPlane / (FarPlane - NearPlane);
        var zOffset = -(NearPlane * FarPlane) / (FarPlane - NearPlane);
        return new Matrix4x4(
            xScale, 0f, 0f, 0f,
            0f, yScale, 0f, 0f,
            0f, 0f, zScale, 1f,
            0f, 0f, zOffset, 0f);
    }

    private void SyncRotation() => Local = Local with { Rotation = RotationFromYawPitch(_yaw, _pitch) };

    /// <summary>
    /// Composes yaw/pitch (radians) into the transform rotation under the
    /// Unity/DirectX convention (X right, Y up, Z forward): yaw turns around
    /// the world up axis and pitch around the camera's right axis. This yields
    /// forward = (sin(yaw)·cos(pitch), sin(pitch), cos(yaw)·cos(pitch)).
    /// </summary>
    private static Rotation RotationFromYawPitch(float yaw, float pitch) =>
        Rotation.FromYaw(yaw * RadiansToDegrees) * Rotation.FromPitch(-pitch * RadiansToDegrees);
}
