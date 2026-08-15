using System.Numerics;

namespace Crowbar.Engine.Tests;

public class LightTests
{
    [Fact]
    public void QueryLight_ReturnsDirectionalAndPointLights()
    {
        using var world = new World();
        var level = world.CreateLevel("Test");
        level.SpawnEntity("Sun").AddComponent<DirectionalLight>();
        level.SpawnEntity("Fill").AddComponent<PointLight>();

        var lights = world.Query<Light>().ToList();
        Assert.Equal(2, lights.Count);
        Assert.IsType<DirectionalLight>(lights[0]);
        Assert.IsType<PointLight>(lights[1]);
    }

    [Fact]
    public void DirectionalLight_DirectionFollowsTheRotation()
    {
        using var world = new World();
        var level = world.CreateLevel("Test");
        var light = level.SpawnEntity("Sun").AddComponent<DirectionalLight>();
        light.Local = new Transform(Vector3.Zero, Rotation.FromYaw(90f), Vector3.One);

        // Forward (+Z) rotated 90 degrees about Y points toward +X.
        var direction = light.Direction;
        Assert.True(direction.Length() > 0.99f);
        Assert.Equal(1f, direction.X, 3);
        Assert.Equal(0f, direction.Y, 3);
        Assert.Equal(0f, direction.Z, 3);
    }

    [Fact]
    public void PointLight_ExposesPositionAndRange()
    {
        using var world = new World();
        var level = world.CreateLevel("Test");
        var light = level.SpawnEntity("Fill").AddComponent<PointLight>();
        light.Local = new Transform(new Vector3(1f, 2f, 3f), Rotation.Identity, Vector3.One);
        light.Range = 5f;
        light.Color = new Vector3(0.4f, 0.6f, 1f);
        light.Intensity = 4f;

        Assert.Equal(new Vector3(1f, 2f, 3f), light.World.Position);
        Assert.Equal(5f, light.Range);
        Assert.Equal(new Vector3(0.4f, 0.6f, 1f), light.Color);
        Assert.Equal(4f, light.Intensity);
    }

    [Fact]
    public void DisabledLights_AreStillGathered_ButSkippedByTheRenderer()
    {
        using var world = new World();
        var level = world.CreateLevel("Test");
        var light = level.SpawnEntity("Off").AddComponent<DirectionalLight>();
        light.Enabled = false;

        // The world gathers them; the renderer filters on Enabled (covered by
        // the UpdateSceneUniforms collection, not unit-tested here).
        var gathered = world.Query<Light>().ToList();
        Assert.Single(gathered);
        Assert.False(gathered[0].Enabled);
    }
}
