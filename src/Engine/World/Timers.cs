namespace Crowbar.Engine.World;

/// <summary>
/// A pending delayed or repeating callback owned by a <see cref="World"/>.
/// Handles are returned by <see cref="TimerSystem.Delay"/> and
/// <see cref="TimerSystem.Repeat"/> so callbacks can be cancelled.
/// </summary>
public sealed class Timer
{
    internal Timer(float interval, Action callback, bool repeat)
    {
        Remaining = interval;
        Interval = interval;
        Callback = callback;
        Repeat = repeat;
    }

    internal float Remaining { get; set; }

    public float Interval { get; }

    public bool Repeat { get; }

    /// <summary>False once the timer fired (one-shot), was cancelled, or repeats stopped.</summary>
    public bool Active { get; internal set; } = true;

    /// <summary>Cancels the timer; a pending callback will not fire.</summary>
    public void Cancel() => Active = false;

    internal Action Callback { get; }
}

/// <summary>
/// World-owned timer queue. Timers only advance while the world is playing
/// (they pause in the editor) and each tick runs in the PostUpdate phase, so
/// they observe the final state of the frame.
/// </summary>
public sealed class TimerSystem
{
    private readonly List<Timer> _timers = [];

    internal TimerSystem(World world)
    {
        World = world;
    }

    public World World { get; }

    /// <summary>Schedules <paramref name="callback"/> to run once after <paramref name="delay"/> seconds.</summary>
    public Timer Delay(float delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new Timer(MathF.Max(0f, delay), callback, repeat: false);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Schedules <paramref name="callback"/> to run every <paramref name="interval"/> seconds.</summary>
    public Timer Repeat(float interval, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new Timer(MathF.Max(0f, interval), callback, repeat: true);
        _timers.Add(timer);
        return timer;
    }

    internal void Update(float deltaTime)
    {
        if (_timers.Count == 0)
            return;

        foreach (var timer in _timers.ToArray())
        {
            if (!timer.Active)
                continue;

            timer.Remaining -= deltaTime;
            while (timer.Active && timer.Remaining <= 0f)
            {
                timer.Callback();
                if (!timer.Repeat)
                {
                    timer.Active = false;
                    break;
                }
                timer.Remaining += timer.Interval;
                if (timer.Interval <= 0f)
                    break; // a zero-interval repeat would otherwise loop forever
            }
        }

        _timers.RemoveAll(t => !t.Active);
    }
}
