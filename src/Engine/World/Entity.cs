namespace Crowbar.Engine.World;

/// <summary>
/// The core object of a <see cref="World"/> (the analog of Unreal's AActor).
/// An entity is a container of <see cref="Component"/>s with an explicit
/// lifecycle; it has no transform of its own (see
/// <see cref="TransformComponent"/>) and no behavior — everything comes from
/// its components. The <see cref="Id"/> is stable for the lifetime of the
/// entity and is the reference used by level serialization, editor selection
/// and networking.
/// </summary>
public sealed class Entity : IDisposable, IValid
{
    private readonly Dictionary<Type, Component> _components = [];
    private bool _destroyed;

    internal Entity(World world, string? name, Level? level)
    {
        World = world;
        Level = level;
        Name = string.IsNullOrWhiteSpace(name) ? $"Entity {world.EntityCount}" : name;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; set; }

    public World World { get; }

    /// <summary>The level this entity belongs to, or null for world-only (non-serialized) entities.</summary>
    public Level? Level { get; internal set; }

    public bool IsDestroyed => _destroyed;

    public bool IsValid => !_destroyed;

    public IReadOnlyCollection<Component> Components => _components.Values;

    /// <summary>Raised when the entity is destroyed; its components have already been stopped and destroyed.</summary>
    public event Action<Entity>? Destroyed;

    public event Action<Entity, Component>? ComponentAdded;

    public event Action<Entity, Component>? ComponentRemoved;

    public T? GetComponent<T>() where T : Component => GetComponent(typeof(T)) as T;

    /// <summary>
    /// Returns the first component whose type is <paramref name="type"/> or
    /// derives from it (Unreal's FindComponentByClass semantics), so asking
    /// for a base type like <see cref="TransformComponent"/> finds concrete
    /// spatial components. The exact-type hit is O(1); the assignable scan is
    /// O(component count). Use <see cref="GetComponents{T}"/> to enumerate
    /// every match.
    /// </summary>
    public Component? GetComponent(Type type)
    {
        if (_components.TryGetValue(type, out var exact))
            return exact;
        foreach (var component in _components.Values)
        {
            if (type.IsAssignableFrom(component.GetType()))
                return component;
        }
        return null;
    }

    /// <summary>Returns every component whose type is <typeparamref name="T"/> or derives from it.</summary>
    public IEnumerable<T> GetComponents<T>() where T : Component
    {
        foreach (var component in _components.Values)
        {
            if (component is T match)
                yield return match;
        }
    }

    public T GetOrAddComponent<T>() where T : Component, new() => GetComponent<T>() ?? AddComponent<T>();

    /// <summary>Creates a component of type <typeparamref name="T"/> and attaches it to this entity.</summary>
    public T AddComponent<T>() where T : Component, new()
    {
        var component = new T();
        AddComponent(component);
        return component;
    }

    /// <summary>
    /// Attaches an existing component. The component must be free (not
    /// attached to another entity) and the entity must not already have one of
    /// that type. Runs <see cref="WorldObject.OnInitialize"/> immediately and
    /// <see cref="WorldObject.OnStart"/> when the world is already playing.
    /// </summary>
    public void AddComponent(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (component.Entity is not null)
            throw new InvalidOperationException(
                $"Component '{component.GetType().Name}' is already attached to entity '{component.Entity.Name}'.");
        if (_destroyed)
            throw new InvalidOperationException($"Entity '{Name}' is destroyed.");
        if (_components.ContainsKey(component.GetType()))
            throw new InvalidOperationException($"Entity '{Name}' already has a '{component.GetType().Name}'.");

        component.Entity = this;
        _components.Add(component.GetType(), component);
        component.IsValid = true;
        component.OnInitialize();
        if (World.IsPlaying)
            World.StartObject(component);
        ComponentAdded?.Invoke(this, component);
    }

    /// <summary>Removes the first component of type <typeparamref name="T"/> (or a type deriving from it).</summary>
    public void RemoveComponent<T>() where T : Component => RemoveComponent(typeof(T));

    public void RemoveComponent(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (GetComponent(type) is { } component)
            RemoveComponent(component);
    }

    /// <summary>
    /// Removes and destroys the component: runs <see cref="WorldObject.OnStop"/>
    /// if it was started, then <see cref="WorldObject.OnDestroy"/>. The
    /// component becomes invalid and its <see cref="Component.Entity"/> is
    /// cleared. Safe to call more than once.
    /// </summary>
    public void RemoveComponent(Component component)
    {
        if (component is null || component.Entity != this)
            return;

        if (component.Started)
        {
            component.Started = false;
            component.OnStop();
        }
        component.OnDestroy();
        _components.Remove(component.GetType());
        component.Entity = null;
        component.IsValid = false;
        ComponentRemoved?.Invoke(this, component);
    }

    /// <summary>
    /// Attaches this entity to <paramref name="parent"/> by attaching their
    /// root <see cref="TransformComponent"/>s. Both entities need a transform
    /// component.
    /// </summary>
    public void AttachTo(Entity parent, bool keepWorldTransform = true)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (parent == this)
            throw new InvalidOperationException("An entity cannot attach to itself.");

        var mine = GetComponent<TransformComponent>()
                   ?? throw new InvalidOperationException($"Entity '{Name}' has no TransformComponent.");
        var theirs = parent.GetComponent<TransformComponent>()
                     ?? throw new InvalidOperationException($"Entity '{parent.Name}' has no TransformComponent.");
        mine.AttachTo(theirs, keepWorldTransform);
    }

    /// <summary>
    /// Destroys the entity: stops and destroys every component, detaches it
    /// from its world and level, then raises <see cref="Destroyed"/>. Safe to
    /// call more than once.
    /// </summary>
    public void Destroy()
    {
        if (_destroyed)
            return;
        _destroyed = true;
        foreach (var component in _components.Values.ToArray())
            RemoveComponent(component);
        World.DetachEntity(this);
        Destroyed?.Invoke(this);
    }

    public void Dispose() => Destroy();
}
