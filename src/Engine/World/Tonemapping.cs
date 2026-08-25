namespace Crowbar.Engine;

/// <summary>Tone curve applied by the <see cref="Tonemapping"/> post-process.</summary>
public enum TonemapOperator
{
    None,
    Reinhard,
    Aces,
    Agx
}

/// <summary>
/// Fullscreen tonemapper: applies the selected tone curve to the whole frame
/// (sky included) after the scene renders in linear HDR. One of several
/// <see cref="PostProcess"/> components; the renderer chains them by
/// <see cref="PostProcess.Order"/>. Settings blend across
/// <see cref="PostProcessVolume"/> instances through
/// <see cref="BasePostProcess{T}.GetWeighted{T}"/>.
/// </summary>
[ComponentIcon("Solar/video/Bold/gallery")]
public sealed class Tonemapping : BasePostProcess<Tonemapping>
{
    [Property]
    public TonemapOperator Operator { get; set; } = TonemapOperator.Aces;

    /// <summary>Exposure in stops (2^exposure), applied before the tone curve.</summary>
    [Property]
    public float Exposure { get; set; }

    public override void Render(PostProcessContext context)
    {
        var @operator = GetWeighted(effect => effect.Operator);
        var exposure = Math.Clamp(GetWeighted(effect => effect.Exposure), -24f, 24f);

        context.Blit(context.Input, context.Output, "Shaders/PostProcesses/Tonemapping.wgsl",
            new RenderAttributes()
                .Set("operator_", (float)@operator)
                .Set("exposure", exposure));
    }
}
