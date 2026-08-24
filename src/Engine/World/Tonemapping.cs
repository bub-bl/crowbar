using System.Numerics;

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
/// <see cref="PostProcess.Order"/>.
/// </summary>
[ComponentIcon("Solar/video/Bold/gallery")]
public sealed class Tonemapping : PostProcess
{
    [Property]
    public TonemapOperator Operator { get; set; } = TonemapOperator.Aces;

    /// <summary>Exposure in stops (2^exposure), applied before the tone curve.</summary>
    [Property]
    public float Exposure { get; set; }

    /// <summary>Post-tonemap saturation multiplier; 1 = unchanged.</summary>
    [Property]
    public float Saturation { get; set; } = 1f;

    internal override string ShaderPath => "Shaders/PostProcesses/Tonemapping.wgsl";

    internal override Vector4 Settings => new((float)Operator, Exposure, Saturation, 0f);
}
