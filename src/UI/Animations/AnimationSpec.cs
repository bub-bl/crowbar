namespace Crowbar.UI;

/// <summary>
/// A single CSS animation: the name of a registered <see cref="Keyframes"/>
/// definition plus its timing descriptors. An element runs a list of these
/// (comma-separated <c>animation</c> values); each entry advances on its own
/// clock and the samples overlay in declaration order.
/// </summary>
public sealed record AnimationSpec(
    string Name = "none",
    float Duration = 0,
    string TimingFunction = "ease",
    float IterationCount = 1,
    string Direction = "normal",
    float Delay = 0,
    string FillMode = "none",
    string PlayState = "running")
{
    /// <summary>True when the entry names a real keyframe definition.</summary>
    public bool HasAnimation => !string.IsNullOrWhiteSpace(Name) &&
                                !Name.Equals("none", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A single CSS transition: the property (or <c>all</c>) it applies to plus its
/// timing descriptors. An element carries a list of these (comma-separated
/// <c>transition</c> values); the transition of each property runs on its own
/// clock with the first matching spec.
/// </summary>
public sealed record TransitionSpec(
    string Property = "none",
    float Duration = 0,
    string TimingFunction = "ease",
    float Delay = 0)
{
    public static readonly TransitionSpec[] None = [new TransitionSpec()];
}

/// <summary>
/// Runtime state of one running keyframe animation on a panel: the spec it was
/// configured with, its own clock (negative while in the delay phase) and the
/// last sampled progress. Kept in sync by <see cref="Panel"/>.
/// </summary>
internal sealed class PanelAnimation
{
    public AnimationSpec Spec = new();
    public float Elapsed;
    public AnimationState State;
    /// <summary>Last computed cycle progress in [0,1], or -1 while there is nothing to sample.</summary>
    public float LastProgress = -1f;
    /// <summary>Cycle progress captured when the animation completed with forwards fill.</summary>
    public float FillProgress;
}

/// <summary>
/// Runtime state of one per-property transition on a panel: the property being
/// interpolated, its from/to values, the timing and its own clock (negative
/// while in the delay phase).
/// </summary>
internal sealed class PropertyTransition
{
    public PropertyTransition(CssProperty property, object? from, object? to, float elapsed, float duration, string timing)
    {
        Property = property;
        From = from;
        To = to;
        Elapsed = elapsed;
        Duration = duration;
        Timing = timing;
    }

    public CssProperty Property { get; }
    public object? From { get; }
    public object? To { get; }
    public float Elapsed;
    public float Duration;
    public string Timing;
}
