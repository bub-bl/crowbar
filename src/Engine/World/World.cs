namespace Crowbar.Engine;

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
/// → <see cref="TickGroup.PostUpdate"/>, then <see cref="Timers"/>. World
/// objects added while playing are started immediately.
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

    /// <summary>All world systems, keyed by type.</summary>
    public IReadOnlyCollection<WorldSystem> Systems => _systems.Values;

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

    /// <summary>Finds a living entity by its stable <see cref="Entity.Id"/>, or null.</summary>
    public Entity? FindEntity(Guid id)
    {
        foreach (var entity in _entities)
        {
            if (entity.Id == id)
                return entity;
        }
        return null;
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

    public T? GetSystem<T>() where T : WorldSystem
        => _systems.GetValueOrDefault(typeof(T)) as T;

    public T GetOrAddSystem<T>() where T : WorldSystem, new()
        => GetSystem<T>() ?? AddSystem(new T());

    /// <summary>
    /// Adds an existing system. The system must be free (not added to another
    /// world) and the world must not already have one of that type. Runs
    /// <see cref="WorldObject.OnInitialize"/> immediately and
    /// <see cref="WorldObject.OnStart"/> when the world is already playing.
    /// </summary>
    public T AddSystem<T>(T system) where T : WorldSystem
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
            StartObject(system);
        return system;
    }

    /// <summary>
    /// Removes and destroys the system: runs <see cref="WorldObject.OnStop"/>
    /// if it was started, then <see cref="WorldObject.OnDestroy"/>. The
    /// system becomes invalid and its <see cref="WorldSystem.World"/> is
    /// cleared. Safe to call more than once.
    /// </summary>
    public void RemoveSystem(WorldSystem system)
    {
        if (system is null || system.World != this)
            return;

        if (system.Started)
        {
            system.Started = false;
            system.OnStop();
        }
        system.OnDestroy();
        _systems.Remove(system.GetType());
        system.World = null;
        system.IsValid = false;
    }

    // ---- Lifecycle ----

    /// <summary>Enters play mode: runs <see cref="WorldObject.OnStart"/> on every world object.</summary>
    public void Start()
    {
        if (_disposed || IsPlaying)
            return;
        IsPlaying = true;
        foreach (var system in _systems.Values.ToArray())
            StartObject(system);
        foreach (var entity in _entities.ToArray())
        {
            foreach (var component in entity.Components.ToArray())
                StartObject(component);
        }
    }

    /// <summary>Leaves play mode: runs <see cref="WorldObject.OnStop"/> on every world object.</summary>
    public void Stop()
    {
        if (_disposed || !IsPlaying)
            return;
        IsPlaying = false;
        foreach (var entity in _entities.ToArray())
        {
            foreach (var component in entity.Components.ToArray())
                StopObject(component);
        }
        foreach (var system in _systems.Values.ToArray())
            StopObject(system);
    }

    /// <summary>
    /// Advances the world by <paramref name="deltaTime"/> seconds. Only has an
    /// effect in play mode — the editor world is never updated.
    /// </summary>
    public void Update(float deltaTime)
    {
        if (_disposed || !IsPlaying)
            return;

        foreach (var system in _systems.Values.ToArray())
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

    internal static void StartObject(WorldObject obj)
    {
        if (obj.Started)
            return;
        obj.Started = true;
        obj.OnStart();
    }

    internal static void StopObject(WorldObject obj)
    {
        if (!obj.Started)
            return;
        obj.Started = false;
        obj.OnStop();
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

    // ---- Teardown ----

    /// <summary>
    /// Stops the world, destroys every entity and disposes every system. All
    /// world objects receive their teardown hooks before the world is gone.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        // Stop first: the guard must still let world objects run their
        // teardown hooks while the world is being torn down.
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
