using System.Numerics;

namespace Crowbar.Engine.Tests;

public class RayTests
{
    private const int Width = 1280;
    private const int Height = 720;

    [Fact]
    public void CenterScreenRay_AimsAlongTheCameraForward()
    {
        var camera = new Camera();

        var ray = Ray.FromScreen(new Vector2(Width / 2f, Height / 2f), Width, Height, camera);

        Assert.Equal(camera.Forward.X, ray.Direction.X, 3);
        Assert.Equal(camera.Forward.Y, ray.Direction.Y, 3);
        Assert.Equal(camera.Forward.Z, ray.Direction.Z, 3);
        Assert.True(ray.Direction.LengthSquared() > 0.99f);
    }

    [Fact]
    public void Ray_HitsACubeBoundsAtTheNearFace()
    {
        var bounds = new Bounds(new Vector3(-0.5f), new Vector3(0.5f));
        var ray = new Ray(new Vector3(0f, 0f, 5f), new Vector3(0f, 0f, -1f));

        Assert.True(ray.Intersects(in bounds, out var distance));
        Assert.Equal(4.5f, distance, 4); // near face at z = 0.5
    }

    [Fact]
    public void Ray_MissesWhenPointingAway()
    {
        var bounds = new Bounds(new Vector3(-0.5f), new Vector3(0.5f));
        var ray = new Ray(new Vector3(0f, 0f, 5f), new Vector3(0f, 0f, 1f));

        Assert.False(ray.Intersects(in bounds, out _));
    }

    [Fact]
    public void CubeModel_HasUnitBounds()
    {
        var model = Model.CreateCube();

        Assert.Equal(new Vector3(-0.5f), model.Bounds.Min);
        Assert.Equal(new Vector3(0.5f), model.Bounds.Max);
    }

    [Fact]
    public void Bounds_TransformByMovesTheBox()
    {
        var bounds = new Bounds(new Vector3(-0.5f), new Vector3(0.5f));

        var world = bounds.TransformBy(Matrix4x4.CreateTranslation(3f, 0f, 0f));

        Assert.Equal(new Vector3(2.5f, -0.5f, -0.5f), world.Min);
        Assert.Equal(new Vector3(3.5f, 0.5f, 0.5f), world.Max);
    }
}
