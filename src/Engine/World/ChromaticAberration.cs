namespace Crowbar.Engine;

/// <summary>Screen-space radial chromatic aberration.</summary>
[ComponentIcon("Solar/video/Bold/camera")]
public sealed class ChromaticAberration : BasePostProcess<ChromaticAberration>
{
    public ChromaticAberration() => Order = 50;

    /// <summary>Maximum spectral separation, from 0 (disabled) to 1.</summary>
    [Property]
    public float Intensity { get; set; }

    /// <summary>Normalized radius where separation begins.</summary>
    [Property]
    public float Start { get; set; } = 0.5f;

    public override void Render(PostProcessContext context)
    {
        var intensity = Math.Clamp(GetWeighted(effect => effect.Intensity), 0f, 1f);
        var start = Math.Clamp(GetWeighted(effect => effect.Start), 0f, 0.999f);
        context.Blit(context.Input, context.Output, "Shaders/PostProcesses/ChromaticAberration.wgsl",
            new RenderAttributes()
                .Set("intensity", intensity)
                .Set("start", start)
                .Set("viewportSize", context.ViewportSize));
    }
}
