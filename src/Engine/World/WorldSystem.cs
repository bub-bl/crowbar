namespace Crowbar.Engine;

/// <summary>
/// World-scoped service (the analog of Unreal's UWorldSubsystem): physics,
/// audio, networking, scripting, etc. A system lives as long as its
/// <see cref="World"/>, is looked up by type through
/// <see cref="World.GetSystem{T}"/> and shares the lifecycle of
/// <see cref="WorldObject"/>, so services pause in the editor too.
/// </summary>
public abstract class WorldSystem : WorldObject, IDisposable
{
    /// <summary>The world that owns this system, or null when not added.</summary>
    public World? World { get; internal set; }

    /// <summary>Removes the system from its world (running its stop/destroy hooks).</summary>
    public void Dispose()
    {
        World?.RemoveSystem(this);
        GC.SuppressFinalize(this);
    }
}
