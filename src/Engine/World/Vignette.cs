namespace Crowbar.Engine;

/// <summary>
/// Darkens the frame corners, applied by the renderer as part of the
/// <see cref="PostProcess"/> chain. Settings blend across
/// <see cref="PostProcessVolume"/> instances through
/// <see cref="BasePostProcess{T}.GetWeighted{T}"/>.
/// </summary>
[ComponentIcon("Solar/video/Bold/camera")]
public sealed class Vignette : BasePostProcess<Vignette>
{
    public Vignette() => Order = 100;

    /// <summary>Corner darkening strength (0 = none).</summary>
    [Property]
    public float Intensity { get; set; } = 0.4f;

    /// <summary>How much of the half-diagonal the darkening covers, from the corners inward (0 = none, 1 = strongest, fading in from the center).</summary>
    [Property]
    public float Radius { get; set; } = 0.65f;

    public override void Render(PostProcessContext context)
    {
        var intensity = Math.Clamp(GetWeighted(effect => effect.Intensity), 0f, 1f);
        var radius = Math.Clamp(GetWeighted(effect => effect.Radius), 0f, 1f);

        context.Blit(context.Input, context.Output, "Shaders/PostProcesses/Vignette.wgsl",
            new RenderAttributes()
                .Set("intensity", intensity)
                .Set("radius", radius)
                .Set("viewportSize", context.ViewportSize));
    }
}
