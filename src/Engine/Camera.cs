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
    private const float DegreesToRadians = MathF.PI / 180f;

    private float _yaw = -MathF.PI / 4f;
    private float _pitch = -0.42f;
    private float _fieldOfView = MathF.PI / 3f;
    private float _nearPlane = 0.1f;
    private float _farPlane = 100f;

    public Camera()
    {
        var position = new Vector3(4.24f, 3f, -4.24f);
        var rotation = RotationFromYawPitch(_yaw, _pitch);
        // Orbit metadata coherent with the starting view: the pivot is the
        // point the camera looks at, at the initial distance. The controller
        // then keeps Position == Pivot - Forward * Distance.
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
    /// World-space point the camera orbits around, driven by the camera
    /// controller. With <see cref="Distance"/> it defines the orbit sphere:
    /// the controller keeps
    /// <c>Position == Pivot - Forward * Distance</c> after every control.
    /// </summary>
    [Property]
    public Vector3 Pivot { get; set; }

    /// <summary>Distance between the camera and <see cref="Pivot"/> (orbit radius).</summary>
    [Property]
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

    /// <summary>
    /// Vertical field of view in degrees (60 by default — the editor's unit,
    /// like the transform rotation). Stored in radians internally; the
    /// projection and the gizmo screen-size math convert back. Clamped to
    /// [1, 179] so the projection can never invert or degenerate.
    /// </summary>
    [Property]
    public float FieldOfView
    {
        get => _fieldOfView * RadiansToDegrees;
        set => _fieldOfView = Math.Clamp(value, 1f, 179f) * DegreesToRadians;
    }

    /// <summary>
    /// Near clip plane distance in world units. Kept strictly below
    /// <see cref="FarPlane"/> so the projection stays valid.
    /// </summary>
    [Property]
    public float NearPlane
    {
        get => _nearPlane;
        set => _nearPlane = Math.Clamp(value, 1e-4f, _farPlane - 1e-4f);
    }

    /// <summary>
    /// Far clip plane distance in world units. Kept strictly above
    /// <see cref="NearPlane"/> so the projection stays valid.
    /// </summary>
    [Property]
    public float FarPlane
    {
        get => _farPlane;
        set => _farPlane = Math.Max(value, _nearPlane + 1e-4f);
    }

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
        // NDC z in [0, 1], WebGPU's depth range. Row-vector layout. The FOV
        // is radians here (the public property is degrees).
        var yScale = 1f / MathF.Tan(_fieldOfView * 0.5f);
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
