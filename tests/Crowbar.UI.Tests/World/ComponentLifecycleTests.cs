namespace Crowbar.Engine.Tests;

/// <summary>Counts every lifecycle hook it receives.</summary>
internal sealed class LifecycleComponent : Component
{
    public int InitializeCount;
    public int StartCount;
    public int UpdateCount;
    public int StopCount;
    public int DestroyCount;

    protected override void OnInitialize() => InitializeCount++;
    protected override void OnStart() => StartCount++;
    protected override void OnUpdate(float deltaTime) => UpdateCount++;
    protected override void OnStop() => StopCount++;
    protected override void OnDestroy() => DestroyCount++;
}

/// <summary>Records its tag into a shared log when it ticks, used to assert tick-group order.</summary>
internal sealed class Ticker : Component
{
    public List<string>? Log { get; set; }
    public string? Tag { get; set; }

    protected override void OnUpdate(float deltaTime) => Log?.Add(Tag ?? "?");
}

public class ComponentLifecycleTests
{
    [Fact]
    public void AddComponent_RunsOnInitializeAndStartsWorld()
    {
        using var world = new World();
        var entity = world.SpawnEntity();
        var component = entity.AddComponent<LifecycleComponent>();

        Assert.Equal(1, component.InitializeCount);
        Assert.Equal(0, component.StartCount);
        Assert.Same(entity, component.Entity);
        Assert.True(component.IsValid);

        world.Start();
        Assert.Equal(1, component.StartCount);
    }

    [Fact]
    public void WorldStartStop_RunOnStartAndOnStopOnEveryComponent()
    {
        using var world = new World();
        var entity = world.SpawnEntity();
        var component = entity.AddComponent<LifecycleComponent>();

        world.Start();
        world.Stop();
        Assert.Equal(1, component.StartCount);
        Assert.Equal(1, component.StopCount);

        world.Start();
        Assert.Equal(2, component.StartCount);
        Assert.Equal(1, component.StopCount);
    }

    [Fact]
    public void Update_TicksOnlyEnabledTickingComponentsInPlayMode()
    {
        using var world = new World();
        var entity = world.SpawnEntity();
        var component = entity.AddComponent<LifecycleComponent>();

        world.Update(1f);
        Assert.Equal(0, component.UpdateCount); // editor: nothing ticks

        world.Start();
        world.Update(1f);
        Assert.Equal(0, component.UpdateCount); // TickEnabled is false by default

        component.TickEnabled = true;
        world.Update(1f);
        Assert.Equal(1, component.UpdateCount);

        component.Enabled = false;
        world.Update(1f);
        Assert.Equal(1, component.UpdateCount); // disabled: no tick
    }

    [Fact]
    public void Update_RunsTickGroupsInOrder()
    {
        using var world = new World();
        var log = new List<string>();

        foreach (var (tag, group) in new[]
                 {
                     ("update", TickGroup.Update),
                     ("pre", TickGroup.PreUpdate),
                     ("post", TickGroup.PostUpdate)
                 })
        {
            var ticker = world.SpawnEntity().AddComponent<Ticker>();
            ticker.Log = log;
            ticker.Tag = tag;
            ticker.TickGroup = group;
            ticker.TickEnabled = true;
        }

        world.Start();
        world.Update(0.016f);

        Assert.Equal(["pre", "update", "post"], log);
    }

    [Fact]
    public void ComponentAddedWhilePlaying_StartsImmediately()
    {
        using var world = new World();
        world.SpawnEntity().AddComponent<LifecycleComponent>();
        world.Start();

        var entity = world.SpawnEntity();
        var component = entity.AddComponent<LifecycleComponent>();
        Assert.Equal(1, component.StartCount);

        world.Update(1f);
        component.TickEnabled = true;
        world.Update(1f);
        Assert.Equal(1, component.UpdateCount);
    }

    [Fact]
    public void RemoveComponent_StopsWhenPlayingThenDestroys()
    {
        using var world = new World();
        var entity = world.SpawnEntity();
        var component = entity.AddComponent<LifecycleComponent>();
        world.Start();

        entity.RemoveComponent(component);
        Assert.Equal(1, component.StopCount);
        Assert.Equal(1, component.DestroyCount);
        Assert.False(component.IsValid);
        Assert.Null(component.Entity);
        Assert.Null(entity.GetComponent<LifecycleComponent>());
    }

    [Fact]
    public void RemoveComponent_NotPlaying_OnlyDestroys()
    {
        using var world = new World();
        var entity = world.SpawnEntity();
        var component = entity.AddComponent<LifecycleComponent>();

        entity.RemoveComponent<LifecycleComponent>();
        Assert.Equal(0, component.StopCount);
        Assert.Equal(1, component.DestroyCount);
    }

    [Fact]
    public void EntityDestroy_RunsDestroyedEventAndDestroysComponents()
    {
        using var world = new World();
        var entity = world.SpawnEntity();
        var component = entity.AddComponent<LifecycleComponent>();
        var destroyed = 0;
        var removed = 0;
        entity.Destroyed += _ => destroyed++;
        entity.ComponentRemoved += (_, _) => removed++;

        entity.Destroy();
        entity.Destroy(); // idempotent

        Assert.Equal(1, destroyed);
        Assert.Equal(1, removed);
        Assert.Equal(1, component.DestroyCount);
        Assert.False(entity.IsValid);
        Assert.True(entity.IsDestroyed);
        Assert.Empty(world.Entities);
    }

    [Fact]
    public void ComponentEvents_FireOnAddAndRemove()
    {
        using var world = new World();
        var entity = world.SpawnEntity();
        var added = 0;
        var removed = 0;
        entity.ComponentAdded += (_, _) => added++;
        entity.ComponentRemoved += (_, _) => removed++;

        var component = entity.AddComponent<LifecycleComponent>();
        Assert.Equal(1, added);

        component.Dispose();
        Assert.Equal(1, removed);
        Assert.Equal(1, component.DestroyCount);
        Assert.Null(entity.GetComponent<LifecycleComponent>());
    }

    [Fact]
    public void GetOrAddComponent_ReturnsExistingInstance()
    {
        using var world = new World();
        var entity = world.SpawnEntity();
        var first = entity.GetOrAddComponent<LifecycleComponent>();
        var second = entity.GetOrAddComponent<LifecycleComponent>();

        Assert.Same(first, second);
    }

    [Fact]
    public void DuplicateComponent_Throws()
    {
        using var world = new World();
        var entity = world.SpawnEntity();
        entity.AddComponent<LifecycleComponent>();

        Assert.Throws<InvalidOperationException>(() => entity.AddComponent<LifecycleComponent>());
    }

    [Fact]
    public void ComponentDisposedTwice_DestroysOnce()
    {
        using var world = new World();
        var entity = world.SpawnEntity();
        var component = entity.AddComponent<LifecycleComponent>();

        component.Dispose();
        component.Dispose();
        Assert.Equal(1, component.DestroyCount);
    }

    [Fact]
    public void WorldDispose_StopsAndDestroysEverything()
    {
        var world = new World();
        var component = world.SpawnEntity().AddComponent<LifecycleComponent>();
        world.Start();

        world.Dispose();
        Assert.Equal(1, component.StopCount);
        Assert.Equal(1, component.DestroyCount);
        Assert.False(world.IsPlaying);
        Assert.Empty(world.Entities);
    }
}
