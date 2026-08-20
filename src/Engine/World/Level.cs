using Crowbar.Engine.Undo;

namespace Crowbar.Engine;

/// <summary>
/// A collection of entities inside a <see cref="World"/> (the analog of
/// Unreal's ULevel). A level is the persistence unit: it maps to one
/// <see cref="LevelFile"/> asset and can be loaded, saved and unloaded
/// independently. A single world can hold several levels; entities spawned
/// without a level belong to the world only and are not serialized.
///
/// The level is also the editor's <em>document</em>: it owns the undo history
/// (<see cref="History"/>, lazily created on first access so raw engine usage
/// stays zero-overhead), and its dirty state is history-derived when the
/// history exists — undo back to the saved position clears the "●", undo away
/// re-sets it. <see cref="ChangeCount"/> counts every mutation so the host can
/// detect mutations that happened outside an undo window (the debug safety
/// net that makes an undo miss impossible to ship).
/// </summary>
public sealed class Level : IDisposable, IValid
{
    private readonly List<Entity> _entities = [];
    private bool _disposed;
    private UndoHistory? _history;
    private bool _dirty;              // classic sticky flag, used until the history exists
    private int _suppressMutations;   // > 0 while the level is being reconstructed (undo, reload)

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
    /// (the editor's title bar "●"). When the undo history exists the dirty
    /// state derives from it — the position moved past the saved position, a
    /// mutation occurred outside any undo window, or an undo window is open —
    /// so undo/redo/save always agree. Without a history (raw engine usage)
    /// it falls back to the classic sticky flag. Every mutation path —
    /// spawning or destroying entities, adding or removing components, moving
    /// a transform (<see cref="TransformComponent.Local"/>) and property
    /// edits through <see cref="InspectorStateBuilder.ApplyEdit"/> — marks
    /// the level dirty; <see cref="ClearDirty"/> is called after a successful
    /// save or load.
    /// </summary>
    public bool IsDirty => _history?.IsDirty ?? _dirty;

    /// <summary>
    /// Monotonic counter of every document mutation (bumped by
    /// <see cref="MarkDirty"/>, i.e. by all five mutation paths). The host
    /// compares it across a frame against the history's open undo windows to
    /// catch mutations that escaped a <c>Step()</c> — the undo "no miss"
    /// detector. Suppressed mutations (undo/redo restores, level reloads) do
    /// not bump it.
    /// </summary>
    public int ChangeCount { get; private set; }

    /// <summary>
    /// The level's undo/redo history, created on first access with the current
    /// state as its baseline (subsequent edits are undoable from there). The
    /// history is document-agnostic (<see cref="UndoHistory"/>); the level
    /// wires it to <see cref="LevelSerializer"/> for capture and restore, so
    /// undo and the save file share the same schema. Any code that reaches an
    /// entity can reach its document's history through
    /// <c>entity.Level?.History</c> — no global to thread through.
    /// </summary>
    public UndoHistory History => _history ??= CreateHistory();

    /// <summary>Marks the level as having unsaved changes and records the mutation.</summary>
    public void MarkDirty()
    {
        // Teardown is not an edit: while the world is being disposed, entity
        // destruction must not look like an unbracketed user mutation (it would
        // trip the host's undo detector on the closing frame).
        if (_suppressMutations > 0 || World.IsDisposed)
            return;
        ChangeCount++;
        _dirty = true;
        _history?.NotifyMutation();
    }

    /// <summary>
    /// Clears the unsaved-changes flag (after a successful save or load). With
    /// a history, this records the current position as the saved state; undoing
    /// past it re-dirties the document.
    /// </summary>
    public void ClearDirty()
    {
        if (_history is { } history)
            history.MarkSaved();
        else
            _dirty = false;
    }

    /// <summary>
    /// Re-baselines the history on the current state (new document loaded or
    /// replaced): the level starts clean with an empty undo stack.
    /// </summary>
    public void ResetHistory() => _history?.Reset();

    /// <summary>
    /// Temporarily suppresses mutation tracking (dirty marking, ChangeCount
    /// and history notification) while the level is being reconstructed by
    /// <see cref="LevelSerializer.ApplyTo"/> — an undo/redo restore must not
    /// look like a new user edit.
    /// </summary>
    internal IDisposable SuppressMutations()
    {
        _suppressMutations++;
        return new MutationSuppression(this);
    }

    private sealed class MutationSuppression : IDisposable
    {
        private Level? _owner;

        public MutationSuppression(Level owner) => _owner = owner;

        public void Dispose()
        {
            var owner = _owner;
            if (owner is null)
                return;
            _owner = null;
            owner._suppressMutations--;
        }
    }

    private UndoHistory CreateHistory() => new(
        () => LevelSerializer.Serialize(this),
        state => LevelSerializer.ApplyTo(this, state));

    /// <summary>Spawns an entity owned by this level (so it will be serialized with it).</summary>
    public Entity SpawnEntity(string? name = null) => World.SpawnEntity(name, this);

    internal void AddEntityInternal(Entity entity)
    {
        _entities.Add(entity);
        MarkDirty();
    }

    internal void RemoveEntityInternal(Entity entity)
    {
        _entities.Remove(entity);
        MarkDirty();
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
