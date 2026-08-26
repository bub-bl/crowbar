namespace Crowbar.Engine;

/// <summary>
/// Temporal anti-aliasing. Accumulates the current frame into the previous
/// frame's history (reprojected through the screen-space motion vectors) and
/// rejects disoccluded history via a neighborhood clamp, removing temporally
/// shimmering edges without the spatial blur of FXAA. Attach to an entity to
/// enable; it reads the velocity buffer produced by the scene pass, so it must
/// run at full scene resolution.
/// </summary>
[ComponentIcon("Solar/code/Bold/timeline")]
public sealed class TemporalAA : BasePostProcess<TemporalAA>
{
    public TemporalAA() => Order = 140;

    /// <summary>History blend weight in [0, 1]; higher accumulates more (smoother, slightly softer).</summary>
    [Property]
    public float Feedback { get; set; } = 0.9f;

    /// <summary>
    /// Sub-pixel projection-jitter amplitude in [0, 1]; lower reduces the
    /// off-center samples TAA averages (0 = no jitter, no anti-aliasing). A
    /// visible way to gauge the effect: raise it while the camera moves.
    /// </summary>
    [Property]
    public float JitterAmount { get; set; } = 1f;

    public override void Render(PostProcessContext context)
    {
        var feedback = Math.Clamp(GetWeighted(effect => effect.Feedback), 0f, 1f);
        context.BlitTemporalAA(context.Input, context.Output, "Shaders/PostProcesses/TemporalAA.wgsl",
            new RenderAttributes()
                .Set("feedback", feedback)
                .Set("jitterOffset", context.JitterPixels)
                .Set("viewportSize", context.ViewportSize));
    }
}