namespace Crowbar.Engine;

/// <summary>
/// A collection of entities inside a <see cref="World"/> (the analog of
/// Unreal's ULevel). A level is the persistence unit: it maps to one
/// <see cref="LevelFile"/> asset and can be loaded, saved and unloaded
/// independently. A single world can hold several levels; entities spawned
/// without a level belong to the world only and are not serialized.
/// </summary>
public sealed class Level : IDisposable, IValid
{
    private readonly List<Entity> _entities = [];
    private bool _disposed;

    internal Level(World world, string? name)
    {
        World = world;
        Name = string.IsNullOrWhiteSpace(name) ? "Level" : name;
    }

    public Guid Id { get; internal set; } = Guid.NewGuid();

    public string Name { get; set; }

    public World World { get; }

    public IReadOnlyList<Entity> Entities => _entities;

    public bool IsValid => !_disposed;

    /// <summary>Raised when an entity is added to this level (e.g. by spawning).</summary>
    public event Action<Entity>? EntityAdded;

    /// <summary>Raised when an entity is removed from this level (e.g. by destruction).</summary>
    public event Action<Entity>? EntityRemoved;

    /// <summary>Spawns an entity owned by this level (so it will be serialized with it).</summary>
    public Entity SpawnEntity(string? name = null) => World.SpawnEntity(name, this);

    internal void AddEntityInternal(Entity entity)
    {
        _entities.Add(entity);
        EntityAdded?.Invoke(entity);
    }

    internal void RemoveEntityInternal(Entity entity)
    {
        _entities.Remove(entity);
        EntityRemoved?.Invoke(entity);
    }

    /// <summary>Destroys every entity in the level and removes it from its world.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var entity in _entities.ToArray())
            World.DestroyEntity(entity);
        _entities.Clear();
        World.RemoveLevelInternal(this);
        GC.SuppressFinalize(this);
    }
}
