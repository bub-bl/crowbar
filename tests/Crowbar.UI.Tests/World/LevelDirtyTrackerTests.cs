using System.Numerics;

namespace Crowbar.Engine.Tests;

public class LevelDirtyTrackerTests
{
    private static Level TrackedLevel(World world)
    {
        var level = world.CreateLevel("Tracked");
        var entity = level.SpawnEntity("Cube");
        entity.AddComponent<MeshRenderer>().Local = new Transform(new Vector3(0f, 0.5f, 0f), Rotation.Identity, Vector3.One);
        LevelDirtyTracker.Track(level);
        return level;
    }

    private static void Untrack()
    {
        LevelDirtyTracker.Untrack();
        Assert.False(LevelDirtyTracker.IsDirty);
    }

    [Fact]
    public void FreshlyTrackedLevel_IsNotDirty()
    {
        using var world = new World();
        TrackedLevel(world);

        Assert.False(LevelDirtyTracker.IsDirty);

        Untrack();
    }

    [Fact]
    public void TransformChange_MarksDirty()
    {
        using var world = new World();
        var level = TrackedLevel(world);
        var entity = level.Entities[0];

        entity.GetComponent<MeshRenderer>()!.Local =
            entity.GetComponent<MeshRenderer>()!.Local.WithPosition(new Vector3(5f, 0f, 0f));

        Assert.True(LevelDirtyTracker.IsDirty);

        Untrack();
    }

    [Fact]
    public void InspectorEdit_MarksDirty()
    {
        using var world = new World();
        var level = TrackedLevel(world);
        var entity = level.Entities[0];
        var mesh = entity.GetComponent<MeshRenderer>()!;
        mesh.Material = Material.FromShader("Surface/StandardPbr").Set("metallic", 0.1f);

        Assert.False(LevelDirtyTracker.IsDirty);
        InspectorStateBuilder.ApplyEdit(entity, "MeshRenderer.Material.metallic", "0.9");
        Assert.True(LevelDirtyTracker.IsDirty);
        Assert.Equal(0.9f, mesh.Material!.Get<float>("metallic"), 3);

        Untrack();
    }

    [Fact]
    public void InspectorEdit_OnUntrackedLevel_DoesNotMarkDirty()
    {
        // The tracker is process-wide: guarantee a clean baseline (a parallel
        // test class cannot have left a level tracked).
        LevelDirtyTracker.Untrack();
        using var world = new World();
        var level = world.CreateLevel("Untracked");
        var entity = level.SpawnEntity("Cube");
        var mesh = entity.AddComponent<MeshRenderer>();
        mesh.Material = Material.FromShader("Surface/StandardPbr");

        InspectorStateBuilder.ApplyEdit(entity, "MeshRenderer.Material.metallic", "0.9");

        Assert.False(LevelDirtyTracker.IsDirty);
    }

    [Fact]
    public void SpawningAndDestroyingEntities_MarksDirty()
    {
        using var world = new World();
        var level = TrackedLevel(world);

        var extra = level.SpawnEntity("Extra");
        Assert.True(LevelDirtyTracker.IsDirty);

        LevelDirtyTracker.Clear();
        Assert.False(LevelDirtyTracker.IsDirty);

        world.DestroyEntity(extra);
        Assert.True(LevelDirtyTracker.IsDirty);

        Untrack();
    }

    [Fact]
    public void AddingAndRemovingComponents_MarksDirty()
    {
        using var world = new World();
        var level = TrackedLevel(world);
        var entity = level.Entities[0];

        var light = entity.AddComponent<PointLight>();
        Assert.True(LevelDirtyTracker.IsDirty);

        LevelDirtyTracker.Clear();
        entity.RemoveComponent(light);
        Assert.True(LevelDirtyTracker.IsDirty);

        Untrack();
    }

    [Fact]
    public void SaveClears_ThenEditMarksDirtyAgain()
    {
        using var world = new World();
        var level = TrackedLevel(world);

        LevelDirtyTracker.Clear();
        Assert.False(LevelDirtyTracker.IsDirty);

        // A later edit re-marks the level.
        level.Entities[0].GetComponent<MeshRenderer>()!.Local =
            level.Entities[0].GetComponent<MeshRenderer>()!.Local.WithScale(new Vector3(2f));
        Assert.True(LevelDirtyTracker.IsDirty);

        Untrack();
    }

    [Fact]
    public void Untrack_StopsHookingAndResetsDirty()
    {
        using var world = new World();
        var level = TrackedLevel(world);
        var entity = level.Entities[0];

        LevelDirtyTracker.Untrack();
        Assert.False(LevelDirtyTracker.IsDirty);

        // Post-untrack mutations must not re-mark (the level is not the open document anymore).
        entity.GetComponent<MeshRenderer>()!.Local =
            entity.GetComponent<MeshRenderer>()!.Local.WithPosition(new Vector3(9f));
        level.SpawnEntity("Late");
        Assert.False(LevelDirtyTracker.IsDirty);
    }

    [Fact]
    public void Track_ReplacesThePreviousLevel()
    {
        using var world = new World();
        var first = TrackedLevel(world);
        var second = world.CreateLevel("Second");
        second.SpawnEntity("Other");

        LevelDirtyTracker.Track(second);
        Assert.Same(second, LevelDirtyTracker.TrackedLevel);
        Assert.False(LevelDirtyTracker.IsDirty);

        // Mutating the old level no longer affects the tracked state.
        first.Entities[0].GetComponent<MeshRenderer>()!.Local =
            first.Entities[0].GetComponent<MeshRenderer>()!.Local.WithPosition(new Vector3(9f));
        Assert.False(LevelDirtyTracker.IsDirty);

        Untrack();
    }
}
