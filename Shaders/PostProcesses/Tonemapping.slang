#language slang 2026;
// Fullscreen tonemapping post-process. The linear HDR scene is tonemapped
// into the next chain texture; the operator is selected per frame by the
// Tonemapping component (0 = none, 1 = Reinhard, 2 = ACES, 3 = AgX). Output
// is linear display-referred; the scene blit (Ui/BlitScene.slang) applies the
// linear -> sRGB display encode when it presents the display texture, so no
// gamma step is needed here.
//
// Convention shared by every post-process shader (Shaders/PostProcesses/ in
// the engine, Content/Shaders/ in a game project): the pass samples the
// previous chain texture at slot 0, declares a sampler at slot 1, and exactly
// one uniform buffer in group 0. The uniform buffer's fields are packed by
// name from the component's [Property] values (a trailing underscore in a
// field name is ignored, so "operator_" matches the "Operator" property), and
// the pass outputs to the render target of the current chain pass.

struct TonemappingUniforms
{
    float operator_; // x = operator index (0..3), matching the TonemapOperator enum
    float exposure;  // exposure in stops (2^exposure), applied before the curve
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<TonemappingUniforms> postProcess;

public struct Tonemapping
{
    // ACES fitted RRT+ODT approximation used widely in real-time engines.
    static const float3x3 AcesInputMat = float3x3(
        0.59719, 0.35458, 0.04823,
        0.07600, 0.90834, 0.01566,
        0.02840, 0.13383, 0.83777);
    static const float3x3 AcesOutputMat = float3x3(
        1.60475, -0.53108, -0.07367,
        -0.10208, 1.10813, -0.00605,
        -0.00327, -0.07276, 1.07602);

    static float3 AcesFit(float3 v)
    {
        return (v * (v + 0.0245786) - 0.000090537) /
            (v * (0.983729 * v + 0.4329510) + 0.238081);
    }

    static float3 Aces(float3 x)
    {
        x = mul(AcesInputMat, max(x, float3(0.0)));
        x = AcesFit(x);
        x = mul(AcesOutputMat, x);
        return clamp(x, float3(0.0), float3(1.0));
    }

    // AgX analytic view transform (same reference constants as Blender,
    // Filament and three.js). Its working primaries are Rec.2020 while this
    // renderer's scene and display intermediates are linear sRGB.
    static const float3x3 AgxSrgbToRec2020 = float3x3(
        0.6274, 0.3293, 0.0433,
        0.0691, 0.9195, 0.0113,
        0.0164, 0.0880, 0.8956);
    static const float3x3 AgxRec2020ToSrgb = float3x3(
        1.6605, -0.5876, -0.0728,
        -0.1246, 1.1329, -0.0083,
        -0.0182, -0.1006, 1.1187);
    static const float3x3 AgxInset = float3x3(
        0.856627153315983, 0.0951212405381588, 0.0482516061458583,
        0.137318972929847, 0.761241990602591, 0.101439036467562,
        0.111898212999950, 0.0767994186031903, 0.811302368396859);
    static const float3x3 AgxOutset = float3x3(
        1.1271005818144368, -0.11060664309660323, -0.016493938717834573,
        -0.14132976349843830, 1.1578237022162720, -0.016493938717834257,
        -0.14132976349843826, -0.11060664309660294, 1.2519364065950405);

    static float3 Agx(float3 x)
    {
        x = mul(AgxSrgbToRec2020, max(x, float3(0.0)));
        x = mul(AgxInset, x);
        x = max(x, 1e-10);
        x = log2(x);
        x = (x + 12.47393) / 16.5;
        x = clamp(x, float3(0.0), float3(1.0));
        let x2 = x * x;
        let x4 = x2 * x2;
        x = 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
        x = mul(AgxOutset, x);
        x = pow(max(x, float3(0.0)), float3(2.2));
        x = mul(AgxRec2020ToSrgb, x);
        return clamp(x, float3(0.0), float3(1.0));
    }

    static float3 Apply(float3 color, float index)
    {
        if (index < 0.5) return color;
        if (index < 1.5) return color / (color + float3(1.0));
        if (index < 2.5) return Aces(color);
        return Agx(color);
    }
};

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[shader("vertex")]
VertexOutput vs_main(uint vertexId : SV_VertexID)
{
    let clip = float2(float((vertexId << 1u) & 2u), float(vertexId & 2u)) * 2.0 - 1.0;
    VertexOutput output;
    output.position = float4(clip, 0.0, 1.0);
    // The scene texture's v origin is top-left, so the uv is flipped against
    // the NDC y (same convention as the surface blit quad).
    output.uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    return output;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    var color = sceneTexture.Sample(sceneSampler, input.uv).rgb * exp2(postProcess.exposure);
    color = Tonemapping::Apply(color, postProcess.operator_);
    return float4(color, 1.0);
}
