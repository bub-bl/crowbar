using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine.Tests;

public class LightGizmoTests
{
    [Fact]
    public void DirectionalLight_DrawsAnArrowAlongItsDirection()
    {
        using var world = new World();
        var level = world.CreateLevel("Test");
        var light = level.SpawnEntity("Sun").AddComponent<DirectionalLight>();
        light.Local = new Transform(Vector3.Zero, Rotation.FromYaw(90f), Vector3.One);

        var batch = new GizmoLineBatch();
        using (Gizmos.Begin(batch, light.Entity))
            light.OnDrawGizmo();

        // The arrow points along the forward direction the light travels,
        // one unit toward +X for a 90° yaw.
        var tip = light.World.Position + light.Direction;
        Assert.Contains(batch.Lines, line => Vector3.Distance(line.End, tip) < 1e-4f);
    }

    [Fact]
    public void LightGizmo_IsSkippedWhenTheEntityIsNotSelected()
    {
        using var world = new World();
        var level = world.CreateLevel("Test");
        var light = level.SpawnEntity("Fill").AddComponent<PointLight>();
        light.Range = 5f;

        var batch = new GizmoLineBatch();
        using (Gizmos.Begin(batch, selectedEntity: null))
            light.OnDrawGizmo();

        Assert.Empty(batch.Lines);
    }

    [Fact]
    public void PointLight_DrawsItsRangeSphere()
    {
        using var world = new World();
        var level = world.CreateLevel("Test");
        var light = level.SpawnEntity("Fill").AddComponent<PointLight>();
        light.Local = new Transform(new Vector3(1f, 2f, 3f), Rotation.Identity, Vector3.One);
        light.Range = 5f;

        var batch = new GizmoLineBatch();
        using (Gizmos.Begin(batch, light.Entity))
            light.OnDrawGizmo();

        Assert.Equal(3 * 32, batch.Count);
        foreach (var line in batch.Lines)
        {
            Assert.Equal(5f, (line.Start - light.World.Position).Length(), 3);
            Assert.Equal(5f, (line.End - light.World.Position).Length(), 3);
        }
    }
}
