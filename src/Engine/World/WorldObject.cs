namespace Crowbar.Engine.World;

/// <summary>
/// Base of every object owned by a <see cref="World"/>: components
/// (<see cref="Component"/>) and world systems (<see cref="WorldSystem"/>).
/// Defines the single lifecycle contract the world drives:
/// <list type="bullet">
/// <item><see cref="OnInitialize"/> — when the object is added.</item>
/// <item><see cref="OnStart"/> — when the world enters play mode (or immediately
/// when added while playing).</item>
/// <item><see cref="OnUpdate"/> — every frame while playing, only when
/// <see cref="Enabled"/> (and, for components,
/// <see cref="Component.TickEnabled"/>) is true.</item>
/// <item><see cref="OnStop"/> — when the world leaves play mode.</item>
/// <item><see cref="OnDestroy"/> — when the object is removed or its owner is
/// destroyed.</item>
/// </list>
/// The editor never starts world objects: they only run in play mode.
/// </summary>
public abstract class WorldObject : IValid
{
    /// <summary>Whether the object participates in the world. Disabled objects stay valid but never update.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True once <see cref="OnStart"/> ran and <see cref="OnStop"/> has not yet run.</summary>
    public bool Started { get; internal set; }

    /// <summary>True while the object is registered in its world.</summary>
    public bool IsValid { get; internal set; }

    protected internal virtual void OnInitialize() { }

    protected internal virtual void OnStart() { }

    protected internal virtual void OnUpdate(float deltaTime) { }

    protected internal virtual void OnStop() { }

    protected internal virtual void OnDestroy() { }
}
