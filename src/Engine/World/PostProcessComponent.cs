namespace Crowbar.Engine;

/// <summary>Tone curve applied by the post-process pass to the whole frame.</summary>
public enum TonemapOperator
{
    None,
    Reinhard,
    Aces,
    Agx
}

/// <summary>
/// Drives the fullscreen post-process — today the frame's tonemapper. The
/// renderer uses the first enabled instance in the world; without one the
/// engine falls back to Reinhard (the historical look), so adding this
/// component is what changes the display transform.
/// </summary>
[ComponentIcon("Solar/video/Bold/gallery")]
public sealed class PostProcessComponent : Component
{
    [Property]
    public TonemapOperator Operator { get; set; } = TonemapOperator.Aces;

    /// <summary>Exposure in stops (2^exposure), applied before the tone curve.</summary>
    [Property]
    public float Exposure { get; set; }

    /// <summary>Post-tonemap saturation multiplier; 1 = unchanged.</summary>
    [Property]
    public float Saturation { get; set; } = 1f;
}
