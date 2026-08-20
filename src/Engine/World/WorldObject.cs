namespace Crowbar.Engine;

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
/// The lifecycle hooks are <c>protected virtual</c> so a component or system can
/// override them from another assembly (a game project's components do this); the
/// world drives them through the internal <see cref="RunInitialize"/>,
/// <see cref="RunStart"/>, <see cref="RunUpdate"/>, <see cref="RunStop"/> and
/// <see cref="RunDestroy"/> bridges. The editor never starts world objects: they
/// only run in play mode.
/// </summary>
public abstract class WorldObject : IValid
{
    /// <summary>Whether the object participates in the world. Disabled objects stay valid but never update.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True once <see cref="OnStart"/> ran and <see cref="OnStop"/> has not yet run.</summary>
    public bool Started { get; internal set; }

    /// <summary>True while the object is registered in its world.</summary>
    public bool IsValid { get; internal set; }

    protected virtual void OnInitialize() { }

    protected virtual void OnStart() { }

    protected virtual void OnUpdate(float deltaTime) { }

    protected virtual void OnStop() { }

    protected virtual void OnDestroy() { }

    // The world drives the lifecycle through these bridges: they are internal so
    // game code cannot call them, only override the hooks above.

    internal void RunInitialize() => OnInitialize();

    internal void RunStart() => OnStart();

    internal void RunUpdate(float deltaTime) => OnUpdate(deltaTime);

    internal void RunStop() => OnStop();

    internal void RunDestroy() => OnDestroy();
}
