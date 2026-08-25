using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// Spatial (FXAA 3.11) anti-aliasing applied by the renderer as the last step
/// of the <see cref="PostProcess"/> chain. It taps the four mid-edge neighbors
/// of every pixel, and where no neighbor contrasts enough it returns the pixel
/// untouched; where it does, it mixes the pixel toward the higher-contrast
/// neighbor so the stair-step edge aliasing of single-sample rendering is
/// feathered over. Being an edge-blend on the display-referred value, it adds
/// no ghosting — it holds nothing across frames.
///
/// Settings blend across <see cref="PostProcessVolume"/> instances through
/// <see cref="BasePostProcess{T}.GetWeighted{T}"/>.
/// </summary>
[ComponentIcon("Solar/ui/Bold/filter")]
public sealed class Fxaa : BasePostProcess<Fxaa>
{
    public Fxaa() => Order = 1000;

    /// <summary>Early-exit contrast gate (0.166 default): a pixel whose largest neighbor contrast is below this is left alone.</summary>
    [Property]
    public float EdgeThreshold { get; set; } = 0.166f;

    /// <summary>How strongly the edge-neighbor blend is applied (0 = off, 1 = full ~50% mix).</summary>
    [Property]
    public float Subpixel { get; set; } = 1f;

    /// <summary>How many search samples the edge walk scans per side (2..16); more smooth longer, softer edges.</summary>
    [Property]
    public float Quality { get; set; } = 5f;

    public override void Render(PostProcessContext context)
    {
        var edgeThreshold = Math.Clamp(GetWeighted(effect => effect.EdgeThreshold), 1e-4f, 1f);
        var subpixel = Math.Clamp(GetWeighted(effect => effect.Subpixel), 0f, 1f);
        var quality = Math.Clamp(GetWeighted(effect => effect.Quality), 2f, 16f);

        var viewport = context.ViewportSize;
        // FXAA's taps are in texels, so watch for degenerate viewport sizes.
        var resolution = new Vector2(1f / Math.Max(1f, viewport.X), 1f / Math.Max(1f, viewport.Y));

        context.Blit(context.Input, context.Output, "Shaders/PostProcesses/Fxaa.wgsl",
            new RenderAttributes()
                .Set("edgeThreshold", edgeThreshold)
                .Set("subpixel", subpixel)
                .Set("quality", quality)
                .Set("resolution", resolution));
    }
}