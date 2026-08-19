namespace Crowbar.Engine;

/// <summary>
/// Tracks whether the editor's open level has unsaved changes (the "●" in the
/// title bar). It hooks the level's own change notifications — entity
/// add/remove (<see cref="Level.EntityAdded"/>/<see cref="Level.EntityRemoved"/>),
/// per-entity component add/remove/destroy and transform edits
/// (<see cref="TransformComponent.LocalChanged"/>) — and every property edit
/// applied through <see cref="InspectorStateBuilder.ApplyEdit"/>. Saving or
/// loading clears the flag (<see cref="Clear"/>); <see cref="MarkDirty"/> is
/// the explicit escape hatch for mutation paths that have no event yet.
///
/// Only one level is tracked at a time (the open document), which matches how
/// the editor works today.
/// </summary>
public static class LevelDirtyTracker
{
    private static Level? _tracked;
    private static bool _dirty;

    /// <summary>True when the tracked level has unsaved changes.</summary>
    public static bool IsDirty => _dirty;

    /// <summary>The level currently being tracked, or null.</summary>
    public static Level? TrackedLevel => _tracked;

    /// <summary>Starts tracking a level (replacing the previous one). The level must not be dirty yet.</summary>
    public static void Track(Level level)
    {
        ArgumentNullException.ThrowIfNull(level);
        Untrack();

        _tracked = level;
        _dirty = false;

        level.EntityAdded += OnEntityAdded;
        level.EntityRemoved += OnEntityRemoved;
        foreach (var entity in level.Entities)
            Hook(entity);
    }

    /// <summary>Stops tracking the current level and forgets its dirty state.</summary>
    public static void Untrack()
    {
        if (_tracked is null)
        {
            _dirty = false;
            return;
        }

        var level = _tracked;
        _tracked = null;
        _dirty = false;

        level.EntityAdded -= OnEntityAdded;
        level.EntityRemoved -= OnEntityRemoved;
        foreach (var entity in level.Entities)
            Unhook(entity);
    }

    /// <summary>Marks the tracked level dirty (no-op when nothing is tracked).</summary>
    public static void MarkDirty()
    {
        if (_tracked is not null)
            _dirty = true;
    }

    /// <summary>Clears the dirty flag — call after a successful save or load.</summary>
    public static void Clear() => _dirty = false;

    /// <summary>
    /// Marks the level dirty when the edited entity belongs to the tracked
    /// level. Called by the editor's write paths (e.g. the inspector) so any
    /// future mutation site automatically reports itself.
    /// </summary>
    public static void NotifyEdit(Entity entity)
    {
        if (entity is not null && ReferenceEquals(entity.Level, _tracked))
            _dirty = true;
    }

    // ---- Event hooks -------------------------------------------------------

    private static void OnEntityAdded(Entity entity)
    {
        MarkDirty();
        Hook(entity);
    }

    private static void OnEntityRemoved(Entity entity)
    {
        MarkDirty();
        Unhook(entity);
    }

    private static void OnEntityDestroyed(Entity entity)
    {
        MarkDirty();
        Unhook(entity);
    }

    private static void OnComponentAdded(Entity entity, Component component)
    {
        MarkDirty();
        Hook(component);
    }

    private static void OnComponentRemoved(Entity entity, Component component)
    {
        MarkDirty();
        Unhook(component);
    }

    private static void OnTransformChanged(TransformComponent transform) => MarkDirty();

    private static void Hook(Entity entity)
    {
        entity.ComponentAdded += OnComponentAdded;
        entity.ComponentRemoved += OnComponentRemoved;
        entity.Destroyed += OnEntityDestroyed;
        foreach (var component in entity.Components)
            Hook(component);
    }

    private static void Unhook(Entity entity)
    {
        entity.ComponentAdded -= OnComponentAdded;
        entity.ComponentRemoved -= OnComponentRemoved;
        entity.Destroyed -= OnEntityDestroyed;
        foreach (var component in entity.Components)
            Unhook(component);
    }

    private static void Hook(Component component)
    {
        if (component is TransformComponent transform)
            transform.LocalChanged += OnTransformChanged;
    }

    private static void Unhook(Component component)
    {
        if (component is TransformComponent transform)
            transform.LocalChanged -= OnTransformChanged;
    }
}
