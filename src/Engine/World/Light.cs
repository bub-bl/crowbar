using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// Base class for lights (the analog of Unreal's ULightComponent). A light is
/// a spatial component: directional lights aim through their rotation, point
/// lights sit at their position. The renderer gathers every light in the
/// world each frame and packs them into the shared light buffer.
/// </summary>
public abstract class Light : TransformComponent
{
    /// <summary>Linear RGB color of the light.</summary>
    public Vector3 Color { get; set; } = Vector3.One;

    /// <summary>Intensity multiplier applied to the color.</summary>
    public float Intensity { get; set; } = 1f;
}
