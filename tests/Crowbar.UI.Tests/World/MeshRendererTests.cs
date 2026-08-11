using System.Numerics;

namespace Crowbar.Engine.Tests;

public class MeshRendererTests
{
    [Fact]
    public void MeshRenderer_IsSpatialAndDrivesWorldTransform()
    {
        using var world = new World();
        var level = world.CreateLevel("Demo");
        var cube = level.SpawnEntity("Cube");
        var renderer = cube.AddComponent<MeshRenderer>();
        renderer.Model = Model.CreateCube();
        renderer.Material = Material.FromShader("Mesh");

        // MeshRenderer is a TransformComponent: it carries the entity's position.
        Assert.IsAssignableFrom<TransformComponent>(renderer);
        Assert.False(renderer.TickEnabled); // static geometry does not tick

        renderer.Local = new Transform(new Vector3(5, 2, 0));

        Assert.Equal(new Vector3(5, 2, 0), renderer.World.Position);
        Assert.Single(world.Query<MeshRenderer>());
        Assert.Contains(cube, level.Entities);
        Assert.Equal(24, renderer.Model!.Meshes[0].Vertices.Length);
    }

    [Fact]
    public void MeshRenderer_DestroyedWithEntity()
    {
        using var world = new World();
        var cube = world.SpawnEntity("Cube");
        var renderer = cube.AddComponent<MeshRenderer>();

        cube.Destroy();

        Assert.False(renderer.IsValid);
        Assert.Empty(world.Query<MeshRenderer>());
    }
}
