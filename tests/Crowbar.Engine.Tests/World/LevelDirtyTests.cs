using System.Numerics;

namespace Crowbar.Engine.Tests;

/// <summary>
/// The dirty flag lives on the <see cref="Level"/> itself (the persistence
/// unit): every mutation path marks it, and save/load clears it. These tests
/// exercise each mutation path directly, without any tracker indirection.
/// </summary>
public class LevelDirtyTests
{
    /// <summary>Builds a small editable level (one meshed cube) inside <paramref name="world"/> and starts it clean.</summary>
    private static (Level Level, Entity Entity) Scene(World world)
    {
        var level = world.CreateLevel("Demo");
        var entity = level.SpawnEntity("Cube");
        entity.AddComponent<MeshRenderer>().Local = new Transform(new Vector3(0f, 0.5f, 0f), Rotation.Identity, Vector3.One);
        level.ClearDirty();
        return (level, entity);
    }

    [Fact]
    public void FreshLevel_IsNotDirty()
    {
        using var world = new World();
        var level = world.CreateLevel("Demo");

        Assert.False(level.IsDirty);
    }

    [Fact]
    public void TransformChange_MarksDirty()
    {
        using var world = new World();
        var (level, entity) = Scene(world);
        var mesh = entity.GetComponent<MeshRenderer>()!;

        mesh.Local = mesh.Local.WithPosition(new Vector3(5f, 0f, 0f));

        Assert.True(level.IsDirty);
    }

    [Fact]
    public void WorldTransformChange_MarksDirty()
    {
        using var world = new World();
        var (level, entity) = Scene(world);
        var mesh = entity.GetComponent<MeshRenderer>()!;

        // Gizmo drags write through World, which recomputes Local.
        mesh.World = mesh.World.WithPosition(new Vector3(5f, 0f, 0f));

        Assert.True(level.IsDirty);
    }

    [Fact]
    public void InspectorEdits_MarkDirty()
    {
        using var world = new World();
        var (level, entity) = Scene(world);
        var mesh = entity.GetComponent<MeshRenderer>()!;
        mesh.Material = Material.FromShader("Surface/StandardPbr").Set("metallic", 0.1f);
        level.ClearDirty();

        InspectorStateBuilder.ApplyEdit(entity, "MeshRenderer.Material.metallic", "0.9");
        Assert.True(level.IsDirty);
        Assert.Equal(0.9f, mesh.Material!.Get<float>("metallic"), 3);

        level.ClearDirty();
        InspectorStateBuilder.ApplyEdit(entity, "transform.position", "1, 2, 3");
        Assert.True(level.IsDirty);
        Assert.Equal(new Vector3(1f, 2f, 3f), mesh.Local.Position);

        var light = entity.AddComponent<PointLight>();
        level.ClearDirty();
        InspectorStateBuilder.ApplyEdit(entity, "PointLight.Range", "42");
        Assert.True(level.IsDirty);
        Assert.Equal(42f, light.Range);
    }

    [Fact]
    public void SpawningAndDestroyingEntities_MarksDirty()
    {
        using var world = new World();
        var (level, _) = Scene(world);

        var extra = level.SpawnEntity("Extra");
        Assert.True(level.IsDirty);

        level.ClearDirty();
        Assert.False(level.IsDirty);

        world.DestroyEntity(extra);
        Assert.True(level.IsDirty);
    }

    [Fact]
    public void AddingAndRemovingComponents_MarksDirty()
    {
        using var world = new World();
        var (level, entity) = Scene(world);

        var light = entity.AddComponent<PointLight>();
        Assert.True(level.IsDirty);

        level.ClearDirty();
        entity.RemoveComponent(light);
        Assert.True(level.IsDirty);
    }

    [Fact]
    public void SaveThenEdit_MarksDirtyAgain()
    {
        using var world = new World();
        var (level, entity) = Scene(world);
        var mesh = entity.GetComponent<MeshRenderer>()!;

        // The save path clears the flag...
        level.ClearDirty();
        Assert.False(level.IsDirty);

        // ...and a later edit re-marks it.
        mesh.Local = mesh.Local.WithScale(new Vector3(2f));
        Assert.True(level.IsDirty);
    }

    [Fact]
    public void WorldOnlyEntity_DoesNotDirtyAnyLevel()
    {
        using var world = new World();
        var (level, _) = Scene(world);

        // An entity without a level is not part of any document: its edits
        // must not mark the open level dirty.
        var orphan = world.SpawnEntity("Orphan");
        orphan.AddComponent<PointLight>().Intensity = 9f;
        orphan.GetComponent<TransformComponent>()!.Local = new Transform(new Vector3(9f), Rotation.Identity, Vector3.One);
        InspectorStateBuilder.ApplyEdit(orphan, "transform.position", "7, 7, 7");

        Assert.False(level.IsDirty);
    }

    [Fact]
    public void EditingOneLevel_DoesNotDirtyAnother()
    {
        using var world = new World();
        var (open, _) = Scene(world);
        var other = world.CreateLevel("Other");
        other.SpawnEntity("OtherEntity");
        other.ClearDirty();

        open.SpawnEntity("Late");

        Assert.True(open.IsDirty);
        Assert.False(other.IsDirty);
    }

    [Fact]
    public void MaterializedLevel_StartsClean()
    {
        using var world = new World();
        var (level, _) = Scene(world);
        level.SpawnEntity("More"); // the source level ends dirty

        var json = LevelSerializer.Serialize(level);
        var loaded = LevelSerializer.CreateLevel(world, LevelSerializer.Deserialize(json));

        // Reconstructing the level mutated it internally; the document still
        // starts clean — the "●" only appears once the user edits it.
        Assert.False(loaded.IsDirty);
        Assert.True(loaded.Entities.Count > 1);

        loaded.SpawnEntity("Edited");
        Assert.True(loaded.IsDirty);
    }
}
