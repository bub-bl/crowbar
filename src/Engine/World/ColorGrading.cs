using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// Analytical color grading applied by the renderer as part of the
/// <see cref="PostProcess"/> chain: white balance (temperature/tint), pivot
/// contrast, brightness, global saturation, then a Unity-style lift / gamma /
/// gain curve per RGB channel. Every control defaults to neutral (the pass is
/// an identity until a value is changed).
///
/// This is the per-pixel ("analytical LUT") flavor of color grading — the
/// operations run straight in the fragment shader on the linear HDR chain
/// texture instead of through a baked 3D LUT. Settings blend across
/// <see cref="PostProcessVolume"/> instances through
/// <see cref="BasePostProcess{T}.GetWeighted{T}"/>.
/// </summary>
[ComponentIcon("Solar/tools/Bold/palette")]
public sealed class ColorGrading : BasePostProcess<ColorGrading>
{
    public ColorGrading() => Order = -1;

    /// <summary>Offsets the black (shadow) range; additive per channel. Neutral is 0.</summary>
    [Property]
    public Vector4 Lift { get; set; } = Vector4.Zero;

    /// <summary>Curves the midrange with a power function per channel. Neutral is 1.</summary>
    [Property]
    public Vector4 Gamma { get; set; } = new Vector4(1f, 1f, 1f, 0f);

    /// <summary>Scales the highlight range; multiplicative per channel. Neutral is 1.</summary>
    [Property]
    public Vector4 Gain { get; set; } = new Vector4(1f, 1f, 1f, 0f);

    /// <summary>White balance: negative cools toward blue, positive warms toward amber (-1..1).</summary>
    [Property]
    public float Temperature { get; set; }

    /// <summary>White balance: negative tints magenta, positive tints green (-1..1).</summary>
    [Property]
    public float Tint { get; set; }

    /// <summary>Global saturation (1 = neutral, 0 = greyscale, 2 = double).</summary>
    [Property]
    public float Saturation { get; set; } = 1f;

    /// <summary>Pivot contrast about mid grey (0 = neutral, -1 = flat, 1 = double).</summary>
    [Property]
    public float Contrast { get; set; }

    /// <summary>Additive brightness offset in the linear HDR domain (0 = neutral).</summary>
    [Property]
    public float Brightness { get; set; }

    public override void Render(PostProcessContext context)
    {
        var lift = GetWeighted(effect => effect.Lift);
        var gamma = GetWeighted(effect => effect.Gamma);
        var gain = GetWeighted(effect => effect.Gain);
        var temperature = Math.Clamp(GetWeighted(effect => effect.Temperature), -1f, 1f);
        var tint = Math.Clamp(GetWeighted(effect => effect.Tint), -1f, 1f);
        var saturation = Math.Clamp(GetWeighted(effect => effect.Saturation), 0f, 2f);
        var contrast = Math.Clamp(GetWeighted(effect => effect.Contrast), -1f, 1f);
        var brightness = GetWeighted(effect => effect.Brightness);

        context.Blit(context.Input, context.Output, "Shaders/PostProcesses/ColorGrading.wgsl",
            new RenderAttributes()
                .Set("lift", lift)
                .Set("gamma", gamma)
                .Set("gain", gain)
                .Set("temperature", temperature)
                .Set("tint", tint)
                .Set("saturation", saturation)
                .Set("contrast", contrast)
                .Set("brightness", brightness));
    }
}