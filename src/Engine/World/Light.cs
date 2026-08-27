using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// Base class for lights (the analog of Unreal's ULightComponent). A light is
/// a spatial component: directional lights aim through their rotation, point
/// lights sit at their position. The renderer gathers every light in the
/// world each frame and packs them into the shared light buffer.
/// </summary>
[ComponentIcon("Solar/devices/Bold/lightbulb")]
public abstract class Light : TransformComponent
{
    /// <summary>Linear RGB color of the light.</summary>
    [Property]
    public Vector3 Color { get; set; } = Vector3.One;

    /// <summary>Intensity multiplier applied to the color.</summary>
    [Property]
    public float Intensity { get; set; } = 1f;

    /// <summary>
    /// When true the light casts shadows: the renderer renders the shadow
    /// casters into a depth map from this light and the lit shaders darken
    /// occluded fragments. Disabled by default (shadow maps are not free).
    /// </summary>
    [Property]
    public bool CastShadows { get; set; }

    /// <summary>
    /// Multiplier on how strongly this light scatters in volumetric fog. Set to
    /// 0 to exclude the light from the fog entirely, or to a value above 1 to
    /// make its shafts in the medium stand out.
    /// </summary>
    [Property]
    public float FogScattering { get; set; } = 1f;

    /// <summary>
    /// Fog-specific tint applied to this light's in-scattering, independent of
    /// <see cref="Color"/>. Defaults to white (neutral = the light's own color).
    /// </summary>
    [Property]
    public Vector3 FogColor { get; set; } = Vector3.One;
}
