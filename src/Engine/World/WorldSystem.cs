namespace Crowbar.Engine.World;

/// <summary>
/// World-scoped service (the analog of Unreal's UWorldSubsystem): physics,
/// audio, networking, scripting, etc. A system lives as long as its
/// <see cref="World"/>, is looked up by type through
/// <see cref="World.GetSubsystem{T}"/> and follows the same
/// start/stop lifecycle as components, so services pause in the editor too.
/// </summary>
public abstract class WorldSystem : IDisposable, IValid
{
    private bool _disposed;

    /// <summary>The world that owns this system. Set when the system is added.</summary>
    public World World { get; internal set; } = null!;

    /// <summary>Whether the system participates in the world. Disabled systems never update.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True once <see cref="OnStart"/> ran and <see cref="OnStop"/> has not yet run.</summary>
    public bool Started { get; internal set; }

    /// <summary>True while the system is registered in its world.</summary>
    public bool IsValid { get; internal set; }

    protected internal virtual void OnInitialize() { }

    protected internal virtual void OnStart() { }

    protected internal virtual void OnUpdate(float deltaTime) { }

    protected internal virtual void OnStop() { }

    protected internal virtual void OnDestroy() { }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Started)
        {
            Started = false;
            OnStop();
        }
        OnDestroy();
        World.RemoveSubsystemInternal(this);
        GC.SuppressFinalize(this);
    }
}
