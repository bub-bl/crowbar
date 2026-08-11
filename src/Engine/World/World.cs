namespace Crowbar.Engine.World;

/// <summary>
/// The runtime container that owns every <see cref="Entity"/>,
/// <see cref="Level"/> and <see cref="WorldSystem"/> (the analog of Unreal's
/// UWorld). A world exists in one of two modes: <b>editing</b> (the default —
/// nothing ticks, no lifecycle beyond construction) and <b>playing</b>
/// (entered through <see cref="Start"/> and left through <see cref="Stop"/>),
/// which is how the integrated editor and the runtime share a single world.
///
/// Frame order in <see cref="Update"/>: world systems first, then the tick
/// groups <see cref="TickGroup.PreUpdate"/> → <see cref="TickGroup.Update"/>
/// → <see cref="TickGroup.PostUpdate"/>, then <see cref="Timers"/>. Entities
/// or components added while playing are started immediately.
/// </summary>
public sealed class World : IDisposable
{
    private readonly List<Entity> _entities = [];
    private readonly List<Level> _levels = [];
    private readonly Dictionary<Type, WorldSystem> _systems = [];
    private bool _disposed;

    public World()
    {
        Timers = new TimerSystem(this);
    }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/> (play mode).</summary>
    public bool IsPlaying { get; private set; }

    public TimerSystem Timers { get; }

    public IReadOnlyList<Entity> Entities => _entities;

    public IReadOnlyList<Level> Levels => _levels;

    public int EntityCount => _entities.Count;

    // ---- Entities ----

    /// <summary>
    /// Creates an entity owned by this world. Pass a <paramref name="level"/>
    /// to make it part of a serializable level; otherwise the entity belongs
    /// to the world only.
    /// </summary>
    public Entity SpawnEntity(string? name = null, Level? level = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(World));
        var entity = new Entity(this, name, level);
        _entities.Add(entity);
        level?.AddEntityInternal(entity);
        return entity;
    }

    public void DestroyEntity(Entity entity)
    {
        if (entity is null || entity.World != this)
            return;
        entity.Destroy();
    }

    internal void DetachEntity(Entity entity)
    {
        _entities.Remove(entity);
        entity.Level?.RemoveEntityInternal(entity);
    }

    // ---- Levels ----

    public Level CreateLevel(string? name = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(World));
        var level = new Level(this, name);
        _levels.Add(level);
        return level;
    }

    public void DestroyLevel(Level level)
    {
        if (level is null || level.World != this)
            return;
        level.Dispose();
    }

    internal void RemoveLevelInternal(Level level) => _levels.Remove(level);

    // ---- Systems ----

    public T? GetSubsystem<T>() where T : WorldSystem
        => _systems.GetValueOrDefault(typeof(T)) as T;

    public T GetOrAddSubsystem<T>() where T : WorldSystem, new()
        => GetSubsystem<T>() ?? AddSubsystem(new T());

    public T AddSubsystem<T>(T system) where T : WorldSystem
    {
        ArgumentNullException.ThrowIfNull(system);
        if (system.World is not null)
            throw new InvalidOperationException($"System '{typeof(T).Name}' is already added to a world.");
        if (_systems.ContainsKey(typeof(T)))
            throw new InvalidOperationException($"World already has a '{typeof(T).Name}' system.");

        system.World = this;
        system.IsValid = true;
        _systems.Add(typeof(T), system);
        system.OnInitialize();
        if (IsPlaying)
            StartSystem(system);
        return system;
    }

    internal void RemoveSubsystemInternal(WorldSystem system)
    {
        if (!_systems.Remove(system.GetType()))
            return;
        system.IsValid = false;
        system.Started = false;
    }

    // ---- Lifecycle ----

    /// <summary>Enters play mode: runs <see cref="Component.OnStart"/> on every component and system.</summary>
    public void Start()
    {
        if (_disposed || IsPlaying)
            return;
        IsPlaying = true;
        foreach (var system in _systems.Values)
            StartSystem(system);
        foreach (var entity in _entities.ToArray())
        {
            foreach (var component in entity.Components.ToArray())
                StartComponent(component);
        }
    }

    /// <summary>Leaves play mode: runs <see cref="Component.OnStop"/> on every component and system.</summary>
    public void Stop()
    {
        if (_disposed || !IsPlaying)
            return;
        IsPlaying = false;
        foreach (var entity in _entities.ToArray())
        {
            foreach (var component in entity.Components.ToArray())
                StopComponent(component);
        }
        foreach (var system in _systems.Values)
            StopSystem(system);
    }

    /// <summary>
    /// Advances the world by <paramref name="deltaTime"/> seconds. Only has an
    /// effect in play mode — the editor world is never updated.
    /// </summary>
    public void Update(float deltaTime)
    {
        if (_disposed || !IsPlaying)
            return;

        foreach (var system in _systems.Values)
        {
            if (system.Enabled)
                system.OnUpdate(deltaTime);
        }

        TickGroupUpdate(TickGroup.PreUpdate, deltaTime);
        TickGroupUpdate(TickGroup.Update, deltaTime);
        TickGroupUpdate(TickGroup.PostUpdate, deltaTime);

        Timers.Update(deltaTime);
    }

    /// <summary>Yields every component of type <typeparamref name="T"/> across the world's living entities.</summary>
    public IEnumerable<T> Query<T>() where T : Component
    {
        foreach (var entity in _entities)
        {
            if (!entity.IsValid)
                continue;
            if (entity.GetComponent<T>() is { } component)
                yield return component;
        }
    }

    private void TickGroupUpdate(TickGroup group, float deltaTime)
    {
        foreach (var entity in _entities.ToArray())
        {
            if (!entity.IsValid)
                continue;
            foreach (var component in entity.Components.ToArray())
            {
                if (component.IsValid && component.Enabled && component.TickEnabled && component.TickGroup == group)
                    component.OnUpdate(deltaTime);
            }
        }
    }

    internal void StartComponent(Component component)
    {
        if (component.Started || !component.IsValid)
            return;
        component.Started = true;
        component.OnStart();
    }

    internal void StopComponent(Component component)
    {
        if (!component.Started)
            return;
        component.Started = false;
        component.OnStop();
    }

    private void StartSystem(WorldSystem system)
    {
        if (system.Started)
            return;
        system.Started = true;
        system.OnStart();
    }

    private void StopSystem(WorldSystem system)
    {
        if (!system.Started)
            return;
        system.Started = false;
        system.OnStop();
    }

    // ---- Teardown ----

    /// <summary>
    /// Stops the world, destroys every entity and disposes every system. All
    /// components receive their teardown hooks before the world is gone.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        // Stop first: the guard must still let components run their teardown
        // hooks while the world is being torn down.
        Stop();
        _disposed = true;
        foreach (var entity in _entities.ToArray())
            entity.Destroy();
        _entities.Clear();
        foreach (var level in _levels.ToArray())
            level.Dispose();
        foreach (var system in _systems.Values.ToArray())
            system.Dispose();
        _systems.Clear();
        GC.SuppressFinalize(this);
    }
}
