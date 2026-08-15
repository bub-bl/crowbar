using System.Numerics;

namespace Crowbar.Engine.Tests;

public class CameraTests
{
    [Fact]
    public void Camera_IsASpatialComponent()
    {
        var camera = new Camera();

        Assert.IsAssignableFrom<TransformComponent>(camera);
        Assert.IsAssignableFrom<Component>(camera);
    }

    [Fact]
    public void Camera_PositionReadsAndWritesTheWorldTransform()
    {
        var camera = new Camera();

        camera.Position = new Vector3(1f, 2f, 3f);

        Assert.Equal(new Vector3(1f, 2f, 3f), camera.World.Position);
        Assert.Equal(new Vector3(1f, 2f, 3f), camera.Position);
        Assert.Equal(new Vector3(1f, 2f, 3f), camera.Local.Position);
    }

    [Fact]
    public void Camera_CanBeAttachedToAnEntityAndFollowsIt()
    {
        using var world = new World();
        var level = world.CreateLevel("Test");
        var entity = level.SpawnEntity("MainCamera");
        var camera = entity.AddComponent<Camera>();

        // Moving the entity moves the camera through the transform hierarchy.
        var gizmoStyleMove = new Transform(
            new Vector3(10f, 5f, 2f), camera.World.Rotation, camera.World.Scale);
        entity.GetComponent<TransformComponent>()!.World = gizmoStyleMove;

        Assert.Equal(new Vector3(10f, 5f, 2f), camera.Position);
    }

    [Fact]
    public void Camera_YawAndPitchDriveTheLookDirection()
    {
        var camera = new Camera();

        camera.Yaw = 0f;
        camera.Pitch = 0f;
        Assert.Equal(new Vector3(0f, 0f, 1f), camera.Forward);

        camera.Yaw = MathF.PI / 2f;
        camera.Pitch = 0f;
        Assert.Equal(1f, camera.Forward.X, 5);
        Assert.Equal(0f, camera.Forward.Y, 5);
        Assert.Equal(0f, camera.Forward.Z, 5);

        camera.Yaw = 0f;
        camera.Pitch = MathF.PI / 2f;
        Assert.Equal(0f, camera.Forward.X, 5);
        Assert.Equal(1f, camera.Forward.Y, 5);
        Assert.Equal(0f, camera.Forward.Z, 5);
    }

    [Fact]
    public void Camera_DefaultLookFacesTheSceneCenter()
    {
        var camera = new Camera();

        // Position (4.24, 3, -4.24) with the default yaw/pitch looks toward the
        // scene origin (the default free camera of the editor).
        var toOrigin = Vector3.Normalize(-camera.Position);
        Assert.True(Vector3.Dot(camera.Forward, toOrigin) > 0.99f);
    }

    [Fact]
    public void Camera_PivotAndDistanceDefineTheOrbitSphere()
    {
        var camera = new Camera();

        // The pivot is the point looked at at the initial distance: the camera
        // rests exactly on its orbit sphere from construction.
        Assert.Equal(camera.Distance, Vector3.Distance(camera.Position, camera.Pivot), 5);
        Assert.True(camera.Distance > 1f);
    }

    [Fact]
    public void Camera_UsesTheUnityDirectXBasisConvention()
    {
        var camera = new Camera();
        camera.Yaw = 0f;
        camera.Pitch = 0f;
        camera.Position = Vector3.Zero;

        // X = right, Y = up, Z = forward (Unity/DirectX).
        Assert.Equal(Vector3.UnitX, camera.Right);
        Assert.Equal(Vector3.UnitY, camera.Up);
        Assert.Equal(Vector3.UnitZ, camera.Forward);

        // The camera's right (screen +X) is indeed the world X axis: a point
        // to the right (in front of the camera) projects to the right side of
        // the screen (positive NDC.x), and a point ahead falls within WebGPU's
        // depth range [0, 1].
        var view = camera.ViewMatrix;
        var proj = camera.ProjectionMatrix(16f / 9f);
        var right = Vector4.Transform(new Vector4(2f, 0f, 5f, 1f), view * proj);
        var ahead = Vector4.Transform(new Vector4(0f, 0f, 5f, 1f), view * proj);
        Assert.True(right.X / right.W > 0f, "world +X must project to the right side of the screen");
        Assert.InRange(ahead.Z / ahead.W, 0f, 1f);
    }
}
