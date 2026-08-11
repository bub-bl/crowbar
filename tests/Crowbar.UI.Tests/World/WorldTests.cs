namespace Crowbar.Engine.World.Tests;

internal sealed class HealthComponent : Component
{
    public int MaxHealth { get; set; } = 100;
}

internal sealed class TestSystem : WorldSystem
{
    public int Updates;
    public int Initializes;
    public int Starts;
    public int Stops;
    public int Destroys;

    protected internal override void OnInitialize() => Initializes++;
    protected internal override void OnStart() => Starts++;
    protected internal override void OnUpdate(float deltaTime) => Updates++;
    protected internal override void OnStop() => Stops++;
    protected internal override void OnDestroy() => Destroys++;
}

public class WorldTests
{
    [Fact]
    public void SpawnEntity_AddsToWorldAndLevel()
    {
        using var world = new World();
        var level = world.CreateLevel("test");
        var entity = level.SpawnEntity("Joueur");

        Assert.Single(world.Entities);
        Assert.Single(level.Entities);
        Assert.Same(level, entity.Level);
        Assert.Equal("Joueur", entity.Name);
        Assert.Same(world, entity.World);
    }

    [Fact]
    public void SpawnEntity_WithoutLevel_IsWorldOnly()
    {
        using var world = new World();
        var entity = world.SpawnEntity();

        Assert.Null(entity.Level);
        Assert.Single(world.Entities);
    }

    [Fact]
    public void DestroyEntity_RemovesFromWorldAndLevel()
    {
        using var world = new World();
        var level = world.CreateLevel();
        var entity = level.SpawnEntity();

        world.DestroyEntity(entity);

        Assert.Empty(world.Entities);
        Assert.Empty(level.Entities);
        Assert.False(entity.IsValid);
    }

    [Fact]
    public void DestroyEntity_FromAnotherWorld_IsIgnored()
    {
        using var worldA = new World();
        using var worldB = new World();
        var entity = worldA.SpawnEntity();

        worldB.DestroyEntity(entity);

        Assert.Single(worldA.Entities);
    }

    [Fact]
    public void Query_ReturnsComponentsAcrossEntities()
    {
        using var world = new World();
        world.SpawnEntity().AddComponent<HealthComponent>();
        world.SpawnEntity().AddComponent<HealthComponent>();
        world.SpawnEntity();

        Assert.Equal(2, world.Query<HealthComponent>().Count());
    }

    [Fact]
    public void Subsystem_LifecycleFollowsWorld()
    {
        using var world = new World();
        var system = world.GetOrAddSubsystem<TestSystem>();

        Assert.Same(system, world.GetSubsystem<TestSystem>());
        Assert.Equal(1, system.Initializes);

        world.Start();
        Assert.True(system.Started);
        Assert.Equal(1, system.Starts);

        world.Update(1f);
        Assert.Equal(1, system.Updates);

        world.Stop();
        Assert.False(system.Started);
        Assert.Equal(1, system.Stops);
    }

    [Fact]
    public void Subsystem_Disabled_DoesNotUpdate()
    {
        using var world = new World();
        var system = world.GetOrAddSubsystem<TestSystem>();
        system.Enabled = false;

        world.Start();
        world.Update(1f);

        Assert.Equal(0, system.Updates);
    }

    [Fact]
    public void Subsystem_RemovedWhilePlaying_StopsAndDestroys()
    {
        using var world = new World();
        var system = world.GetOrAddSubsystem<TestSystem>();
        world.Start();

        system.Dispose();

        Assert.Equal(1, system.Stops);
        Assert.Equal(1, system.Destroys);
        Assert.False(system.IsValid);
        Assert.Null(world.GetSubsystem<TestSystem>());
    }

    [Fact]
    public void DestroyLevel_DestroysItsEntities()
    {
        using var world = new World();
        var level = world.CreateLevel("test");
        var entity = level.SpawnEntity();

        world.DestroyLevel(level);

        Assert.False(level.IsValid);
        Assert.False(entity.IsValid);
        Assert.Empty(world.Levels);
        Assert.Empty(world.Entities);
    }

    [Fact]
    public void Timer_Delay_FiresOnceAfterElapsedTime()
    {
        using var world = new World();
        var fired = 0;
        world.Timers.Delay(1f, () => fired++);

        world.Start();
        world.Update(0.5f);
        Assert.Equal(0, fired);

        world.Update(0.5f);
        Assert.Equal(1, fired);

        world.Update(2f);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Timer_Repeat_FiresEveryInterval()
    {
        using var world = new World();
        var count = 0;
        world.Timers.Repeat(0.5f, () => count++);

        world.Start();
        world.Update(1.25f);

        Assert.Equal(2, count);
    }

    [Fact]
    public void Timer_Cancel_PreventsCallback()
    {
        using var world = new World();
        var fired = 0;
        var timer = world.Timers.Delay(1f, () => fired++);

        world.Start();
        timer.Cancel();
        world.Update(2f);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Timer_OnlyAdvancesWhilePlaying()
    {
        using var world = new World();
        var fired = 0;
        world.Timers.Delay(0.1f, () => fired++);

        world.Update(1f); // editor: no timers
        Assert.Equal(0, fired);

        world.Start();
        world.Update(1f);
        Assert.Equal(1, fired);
    }
}
