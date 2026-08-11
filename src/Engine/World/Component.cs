namespace Crowbar.Engine;

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
/// spatial components derive from <see cref="TransformComponent"/>. Lifecycle
/// hooks come from <see cref="WorldObject"/>.
/// </summary>
public abstract class Component : WorldObject, IDisposable
{
    /// <summary>The entity this component is attached to, or null when not attached.</summary>
    public Entity? Entity { get; internal set; }

    /// <summary>Whether <see cref="WorldObject.OnUpdate"/> should run each frame while playing.</summary>
    public bool TickEnabled { get; set; }

    /// <summary>The tick group this component updates in (ignored when <see cref="TickEnabled"/> is false).</summary>
    public TickGroup TickGroup { get; set; } = TickGroup.Update;

    /// <summary>Removes and destroys the component through its entity.</summary>
    public void Dispose()
    {
        Entity?.RemoveComponent(this);
        GC.SuppressFinalize(this);
    }
}
