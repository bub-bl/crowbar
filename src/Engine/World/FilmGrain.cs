namespace Crowbar.Engine;

/// <summary>Temporally animated, luminance-aware monochrome film grain.</summary>
[ComponentIcon("Solar/video/Bold/film")]
public sealed class FilmGrain : BasePostProcess<FilmGrain>
{
    public FilmGrain() => Order = 150;

    /// <summary>Grain strength from 0 (disabled) to 1.</summary>
    [Property]
    public float Intensity { get; set; }

    /// <summary>How strongly bright areas suppress grain, from 0 to 1.</summary>
    [Property]
    public float Response { get; set; } = 0.8f;

    public override void Render(PostProcessContext context)
    {
        var intensity = Math.Clamp(GetWeighted(effect => effect.Intensity), 0f, 1f);
        var response = Math.Clamp(GetWeighted(effect => effect.Response), 0f, 1f);
        context.Blit(context.Input, context.Output, "Shaders/PostProcesses/FilmGrain.wgsl",
            new RenderAttributes()
                .Set("intensity", intensity)
                .Set("response", response)
                .Set("time", context.Time)
                .Set("viewportSize", context.ViewportSize));
    }
}
