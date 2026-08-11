namespace Crowbar.Engine.World;

/// <summary>
/// Base class for every spatial component (the analog of Unreal's
/// USceneComponent). Holds a <see cref="Local"/> transform relative to a
/// parent and composes it into a world transform on demand through the parent
/// chain. An entity is spatial when it owns at least one of these; purely
/// logical components (game rules, inventory, state) do not derive from it.
///
/// Attachment is component-to-component: meshes, cameras and lights attach to
/// each other. <see cref="AttachTo"/> and <see cref="Detach"/> support the
/// keep-world-transform / keep-local-transform rules of Unreal's
/// attachment system.
/// </summary>
public abstract class TransformComponent : Component
{
    private Transform _local = Transform.Zero;
    private readonly List<TransformComponent> _children = [];

    /// <summary>Transform relative to <see cref="Parent"/>, or absolute when the component is a root.</summary>
    public Transform Local
    {
        get => _local;
        set
        {
            if (_local == value)
                return;
            _local = value;
            LocalChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// World-space transform, composed from the parent chain on demand.
    /// Setting it recomputes <see cref="Local"/> so the component keeps its
    /// world position while moving through the hierarchy.
    /// </summary>
    public Transform World
    {
        get => Parent is null ? _local : Parent.World.ToWorld(_local);
        set => Local = Parent is null ? value : Parent.World.ToLocal(value);
    }

    public TransformComponent? Parent { get; private set; }

    public IReadOnlyList<TransformComponent> Children => _children;

    public bool HasParent => Parent is not null;

    /// <summary>Raised whenever <see cref="Local"/> changes (including through <see cref="World"/> and attachment).</summary>
    public event Action<TransformComponent>? LocalChanged;

    /// <summary>
    /// Attaches this component to <paramref name="parent"/>. With
    /// <paramref name="keepWorldTransform"/> (default) the component stays at
    /// its current world position and <see cref="Local"/> is recomputed;
    /// otherwise <see cref="Local"/> is kept as-is and the component may move
    /// in world space.
    /// </summary>
    public void AttachTo(TransformComponent parent, bool keepWorldTransform = true)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (parent == this)
            throw new InvalidOperationException("A transform component cannot attach to itself.");
        if (parent.IsDescendantOf(this))
            throw new InvalidOperationException("Attaching would create a cycle in the transform hierarchy.");

        var world = World;
        Detach(keepWorldTransform: false);
        Parent = parent;
        parent._children.Add(this);
        if (keepWorldTransform)
            Local = parent.World.ToLocal(world);
    }

    /// <summary>
    /// Detaches this component from its parent. With
    /// <paramref name="keepWorldTransform"/> (default) the component stays at
    /// its current world position and <see cref="Local"/> becomes absolute.
    /// </summary>
    public void Detach(bool keepWorldTransform = true)
    {
        if (Parent is null)
            return;
        var world = World;
        Parent._children.Remove(this);
        Parent = null;
        if (keepWorldTransform)
            Local = world;
    }

    /// <summary>True when this component sits somewhere below <paramref name="ancestor"/> in the hierarchy.</summary>
    public bool IsDescendantOf(TransformComponent ancestor)
    {
        for (var current = Parent; current is not null; current = current.Parent)
        {
            if (current == ancestor)
                return true;
        }
        return false;
    }

    /// <summary>Detaches every child (keeping their world transforms) so destruction never drags them along.</summary>
    protected internal override void OnDestroy()
    {
        base.OnDestroy();
        foreach (var child in _children.ToArray())
            child.Detach(keepWorldTransform: true);
    }
}
