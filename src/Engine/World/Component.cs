namespace Crowbar.Engine.World;

/// <summary>
/// Tick order inside a single <see cref="World.Update"/> call, the analog of
/// Unreal's tick groups. Components opt into one group through
/// <see cref="Component.TickGroup"/>.
/// </summary>
public enum TickGroup
{
    /// <summary>Physics, input simulation, incoming network.</summary>
    PreUpdate,

    /// <summary>Gameplay.</summary>
    Update,

    /// <summary>Camera, outgoing network, UI-facing state.</summary>
    PostUpdate
}

/// <summary>
/// Base class for everything attached to an <see cref="Entity"/> (the analog
/// of Unreal's UActorComponent). A component has no transform of its own;
/// spatial components derive from <see cref="TransformComponent"/>.
///
/// Lifecycle, driven by the owning <see cref="World"/>:
/// <list type="bullet">
/// <item><see cref="OnInitialize"/> — when the component is added.</item>
/// <item><see cref="OnStart"/> — when the world enters play mode (or immediately
/// when added while playing).</item>
/// <item><see cref="OnUpdate"/> — every frame while playing, only when
/// <see cref="Enabled"/> and <see cref="TickEnabled"/> are true.</item>
/// <item><see cref="OnStop"/> — when the world leaves play mode.</item>
/// <item><see cref="OnDestroy"/> — when the component is removed or its entity
/// is destroyed.</item>
/// </list>
/// The editor never calls <see cref="OnStart"/> or <see cref="OnUpdate"/>:
/// entities only tick in play mode.
/// </summary>
public abstract class Component : IDisposable, IValid
{
    private bool _disposed;

    /// <summary>The entity this component is attached to, or null when not attached.</summary>
    public Entity? Entity { get; internal set; }

    /// <summary>Whether the component participates in the world. Disabled components stay valid but never tick.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether <see cref="OnUpdate"/> should run each frame while playing.</summary>
    public bool TickEnabled { get; set; }

    /// <summary>The tick group this component updates in (ignored when <see cref="TickEnabled"/> is false).</summary>
    public TickGroup TickGroup { get; set; } = TickGroup.Update;

    /// <summary>True once <see cref="OnStart"/> ran and <see cref="OnStop"/> has not yet run.</summary>
    public bool Started { get; internal set; }

    /// <summary>True while the component is attached to a living entity.</summary>
    public bool IsValid { get; internal set; }

    protected internal virtual void OnInitialize() { }

    protected internal virtual void OnStart() { }

    protected internal virtual void OnUpdate(float deltaTime) { }

    protected internal virtual void OnStop() { }

    protected internal virtual void OnDestroy() { }

    /// <summary>Removes and destroys the component through its entity.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Entity?.RemoveComponent(this);
        GC.SuppressFinalize(this);
    }
}
