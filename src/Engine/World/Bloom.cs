using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// Gaussian-pyramid tone bloom in the style of Unreal and Unity: bright pixels
/// are extracted from the linear HDR scene, downsampled into a half-res pyramid,
/// blurred outward through an upsampling cascade, and finally added back onto
/// the scene before the rest of the post-process chain (so it composes with
/// tonemapping). Settings blend across <see cref="PostProcessVolume"/> instances
/// through <see cref="BasePostProcess{T}.GetWeighted{T}"/>.
/// </summary>
[ComponentIcon("Solar/video/Bold/brightness")]
public sealed class Bloom : BasePostProcess<Bloom>
{
    private const string ShaderPath = "Shaders/PostProcesses/";
    private const int MaxPyramidLevels = 4;

    /// <summary>Overall glow amount (0 = no bloom).</summary>
    [Property]
    public float Intensity { get; set; } = 1f;

    /// <summary>Luminance above which pixels begin to bloom.</summary>
    [Property]
    public float Threshold { get; set; } = 1f;

    /// <summary>Soft-knee fraction of <see cref="Threshold"/> (0..1).</summary>
    [Property]
    public float ThresholdKnee { get; set; } = 0.5f;

    /// <summary>Contribution of coarser pyramid levels (0..1); higher values produce wider halos.</summary>
    [Property]
    public float Scatter { get; set; } = 0.7f;

    /// <summary>Pyramid depth (1..4): more levels give wider, softer halos at the cost of passes.</summary>
    [Property]
    public int DownsampleCount { get; set; } = 4;

    /// <summary>Upper clamp on extracted brightness (0 disables); prevents fireflies from saturating a pixel.</summary>
    [Property]
    public float Clamp { get; set; } = 3.5f;

    public override void Render(PostProcessContext context)
    {
        var intensity = Math.Max(0f, GetWeighted(effect => effect.Intensity));
        var threshold = Math.Max(0f, GetWeighted(effect => effect.Threshold));
        var knee = threshold * Math.Clamp(GetWeighted(effect => effect.ThresholdKnee), 0f, 1f);
        var scatter = Math.Clamp(GetWeighted(effect => effect.Scatter), 0f, 1f);
        var count = Math.Clamp(GetWeighted(effect => effect.DownsampleCount), 1, MaxPyramidLevels);
        var clamp = Math.Max(0f, GetWeighted(effect => effect.Clamp));

        // 1. Extract the brights into the finest (half-res) pyramid level.
        context.Blit(context.Input, context.GetBloomLevelTexture(0), ShaderPath + "BloomPrefilter.wgsl",
            new RenderAttributes()
                .Set("threshold", threshold)
                .Set("thresholdKnee", knee)
                .Set("clamp", clamp));

        // 2. Downsample the pyramid: each level averages a 2x2 block of the finer one.
        for (var i = 1; i < count; i++)
        {
            var invTexel = new Vector2(1f / context.GetBloomLevelWidth(i - 1), 1f / context.GetBloomLevelHeight(i - 1));
            context.Blit(context.GetBloomLevelTexture(i - 1), context.GetBloomLevelTexture(i),
                ShaderPath + "BloomDownsample.wgsl",
                new RenderAttributes().Set("invTexel", invTexel));
        }

        // 3. Upsample cascade: tent-filter each coarse level and add it into the
        //    next finer level. Preserving the fine level is what retains small,
        //    sharp highlights while the coarse levels form broad halos.
        for (var i = count - 1; i >= 1; i--)
        {
            var invTexel = new Vector2(
                1f / context.GetBloomLevelWidth(i),
                1f / context.GetBloomLevelHeight(i));
            context.BlitAdditive(context.GetBloomLevelTexture(i), context.GetBloomLevelTexture(i - 1),
                ShaderPath + "BloomUpsample.wgsl",
                new RenderAttributes()
                    .Set("invTexel", invTexel)
                    .Set("scatter", scatter));
        }

        // 4. Final combine: add the re-composed glow onto the original scene.
        context.Blit(context.Input, context.Output, ShaderPath + "BloomCombine.wgsl",
            new RenderAttributes().Set("intensity", intensity),
            context.GetBloomLevelTexture(0));
    }
}
