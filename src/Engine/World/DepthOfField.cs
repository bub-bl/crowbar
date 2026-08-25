using System.Numerics;

namespace Crowbar.Engine;

/// <summary>Physical gather depth of field with separate near and far layers.</summary>
[ComponentIcon("Solar/video/Bold/camera")]
public sealed class DepthOfField : BasePostProcess<DepthOfField>
{
    public DepthOfField() => Order = -50;

    /// <summary>Distance from the camera to the focal plane, in world units.</summary>
    [Property]
    public float FocusDistance { get; set; } = 10f;

    /// <summary>Lens focal length in millimeters.</summary>
    [Property]
    public float FocalLength { get; set; } = 50f;

    /// <summary>Lens f-number; lower values produce shallower depth of field.</summary>
    [Property]
    public float Aperture { get; set; } = 5.6f;

    /// <summary>Physical sensor height in millimeters.</summary>
    [Property]
    public float SensorHeight { get; set; } = 24f;

    /// <summary>Maximum circle-of-confusion radius in full-resolution pixels.</summary>
    [Property]
    public float MaxBlurRadius { get; set; } = 12f;

    public override void Render(PostProcessContext context)
    {
        var focusDistance = Math.Max(GetWeighted(effect => effect.FocusDistance), context.NearPlane + 1e-3f);
        var focalLength = Math.Clamp(GetWeighted(effect => effect.FocalLength), 1f, 300f);
        var aperture = Math.Clamp(GetWeighted(effect => effect.Aperture), 0.7f, 32f);
        var sensorHeight = Math.Clamp(GetWeighted(effect => effect.SensorHeight), 1f, 100f);
        var maxBlurRadius = Math.Clamp(GetWeighted(effect => effect.MaxBlurRadius), 1f, 32f);
        var lens = new Vector4(focusDistance, focalLength, aperture, sensorHeight);
        var depth = new Vector2(context.NearPlane, context.FarPlane);
        var halfTexel = new Vector2(2f / context.ViewportSize.X, 2f / context.ViewportSize.Y);

        var coc = context.GetDepthOfFieldTexture(0);
        var far = context.GetDepthOfFieldTexture(1);
        var near = context.GetDepthOfFieldTexture(2);

        context.Blit(context.Input, coc, "Shaders/PostProcesses/DepthOfFieldPrefilter.wgsl",
            new RenderAttributes()
                .Set("lens", lens)
                .Set("depthRange", depth)
                .Set("viewportSize", context.ViewportSize)
                .Set("maxBlurRadius", maxBlurRadius));

        var blurAttributes = new RenderAttributes()
            .Set("invTexel", halfTexel)
            .Set("maxBlurRadius", maxBlurRadius);
        context.Blit(coc, far, "Shaders/PostProcesses/DepthOfFieldBlur.wgsl", blurAttributes.Set("layer", 1f));
        context.Blit(coc, near, "Shaders/PostProcesses/DepthOfFieldBlur.wgsl", new RenderAttributes()
            .Set("invTexel", halfTexel)
            .Set("maxBlurRadius", maxBlurRadius)
            .Set("layer", -1f));

        var intermediate = context.GetScratchTexture(0);
        context.Blit(context.Input, intermediate, "Shaders/PostProcesses/DepthOfFieldCompositeFar.wgsl",
            new RenderAttributes()
                .Set("lens", lens)
                .Set("depthRange", depth)
                .Set("viewportSize", context.ViewportSize)
                .Set("maxBlurRadius", maxBlurRadius), far);
        context.Blit(intermediate, context.Output, "Shaders/PostProcesses/DepthOfFieldCompositeNear.wgsl", null, near);
    }
}