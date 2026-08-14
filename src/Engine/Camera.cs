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
        var position = new Vector3(4.24f, 3f, 4.24f);
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

    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);

    public Matrix4x4 ProjectionMatrix(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect, NearPlane, FarPlane);

    private void SyncRotation() => Local = Local with { Rotation = RotationFromYawPitch(_yaw, _pitch) };

    /// <summary>
    /// Composes yaw/pitch (radians) into the transform rotation: yaw around the
    /// world up axis, then pitch around the camera's right axis. This yields
    /// forward = (sin(yaw)·cos(pitch), sin(pitch), -cos(yaw)·cos(pitch)).
    /// </summary>
    private static Rotation RotationFromYawPitch(float yaw, float pitch) =>
        Rotation.FromYaw(-yaw * RadiansToDegrees) * Rotation.FromPitch(pitch * RadiansToDegrees);
}
