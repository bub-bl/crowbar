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

    /// <summary>
    /// True when the level has unsaved changes since its last save or load
    /// (the editor's title bar "●"). Every mutation path — spawning or
    /// destroying entities, adding or removing components, moving a transform
    /// (<see cref="TransformComponent.Local"/>) and property edits through
    /// <see cref="InspectorStateBuilder.ApplyEdit"/> — marks the level dirty;
    /// <see cref="ClearDirty"/> is called after a successful save or load.
    /// </summary>
    public bool IsDirty { get; private set; }

    /// <summary>Marks the level as having unsaved changes.</summary>
    public void MarkDirty() => IsDirty = true;

    /// <summary>Clears the unsaved-changes flag (after a successful save or load).</summary>
    public void ClearDirty() => IsDirty = false;

    /// <summary>Spawns an entity owned by this level (so it will be serialized with it).</summary>
    public Entity SpawnEntity(string? name = null) => World.SpawnEntity(name, this);

    internal void AddEntityInternal(Entity entity)
    {
        _entities.Add(entity);
        IsDirty = true;
    }

    internal void RemoveEntityInternal(Entity entity)
    {
        _entities.Remove(entity);
        IsDirty = true;
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
