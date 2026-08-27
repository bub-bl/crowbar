using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// Global volumetric-fog controller (the analog of s&amp;box's
/// <c>VolumetricFogController</c>). When enabled, the renderer ray-marches the
/// camera frustum into a coarse froxel volume each frame, accumulates the light
/// in-scattered by the scene's <see cref="VolumetricFogVolume"/> boxes, and
/// blends the result into the image before the final tonemapping/display steps.
///
/// On its own, an empty scene has nothing to scatter: the fog is *shaped* by the
/// <see cref="VolumetricFogVolume"/> density boxes, which provide density and a
/// per-volume tint. This component tunes how that density scatters the scene's
/// lights (anisotropy and scattering coefficient), how far the fog reaches, and
/// how close to the camera it fades in.
/// </summary>
[ComponentIcon("Solar/weather/Bold/fog")]
public sealed class VolumetricFog : BasePostProcess<VolumetricFog>
{
    public VolumetricFog() => Order = 300;

    /// <summary>Master density scale applied to every fog volume.</summary>
    [Property]
    public float Density { get; set; } = 0.02f;

    /// <summary>Coefficient scaling how strongly the volumetric light scatters toward the eye.</summary>
    [Property]
    public float Scattering { get; set; } = 0.8f;

    /// <summary>Henyey-Greenstein anisotropy (<c>0</c> isotropic, positive forward-scattering toward the sun).</summary>
    [Property]
    public float Anisotropy { get; set; } = 0.2f;

    /// <summary>Visible range of the fog volume: geometry beyond this is fully fogged out.</summary>
    [Property]
    public float DrawDistance { get; set; } = 3000f;

    /// <summary>Depth at which the fog starts fading in (nothing is fogged closer than this).</summary>
    [Property]
    public float FadeInStart { get; set; } = 64f;

    /// <summary>Depth at which the fog has fully faded in.</summary>
    [Property]
    public float FadeInEnd { get; set; } = 256f;

    /// <summary>Ambient scatter fill added to fully shadowed fog so it reads as airy volume.</summary>
    [Property]
    public float Ambient { get; set; } = 0.02f;

    /// <summary>Vertical/horizontal resolution of the frustum slice grid, as a fraction of the viewport.</summary>
    [Property]
    public float ResolutionScale { get; set; } = 0.25f;

    /// <summary>Number of depth slices across <see cref="DrawDistance"/>.</summary>
    [Property]
    public int SliceCount { get; set; } = 32;

    public override void Render(PostProcessContext context)
    {
        // The pass chain input is the scene here; drive the whole fog in one
        // call so the renderer can run the accumulate/integrate compute passes
        // and the apply composite without the three-way CPU round-trip being
        // observable to the outside.
        context.ApplyVolumetricFog(this);
    }

    // Blend helpers used by tests / volume weighting. The heavier per-frame
    // values are read directly on the entry point so volumes win naturally.
    internal (float Density, float Scattering, float Anisotropy, float DrawDistance,
              float FadeInStart, float FadeInEnd, float Ambient, float ResolutionScale, int SliceCount) Snapshot()
        => (GetWeighted(e => e.Density),
            GetWeighted(e => e.Scattering),
            GetWeighted(e => e.Anisotropy),
            GetWeighted(e => e.DrawDistance),
            GetWeighted(e => e.FadeInStart),
            GetWeighted(e => e.FadeInEnd),
            GetWeighted(e => e.Ambient),
            GetWeighted(e => e.ResolutionScale),
            (int)GetWeighted(e => e.SliceCount));
}