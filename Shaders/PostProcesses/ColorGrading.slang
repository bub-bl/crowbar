#language slang 2026;
// Analytical color grading post-process: white balance (temperature/tint),
// pivot contrast, brightness, global saturation, then a Unity-style lift /
// gamma / gain curve applied per RGB channel in the linear HDR domain.
//
// The lift/gamma/gain step follows HDRP's LutBuilder3D.compute exactly:
//
//   color = color * gain.xyz + lift.xyz;
//   color = sign(color) * pow(abs(color), gamma.xyz);
//
// Lift offsets the shadows, gamma powers the mids (1 = neutral), gain scales
// the highlights. Every control defaults to neutral, so the pass is an
// identity until values are changed.
//
// This is the analytical variant of a LUT grade (no 3D LUT texture): the
// operations run per-pixel and the "LUT" is implicit. It reads the previous
// chain texture with the usual post-process convention: texture at slot 0,
// sampler at slot 1, one uniform buffer in group 0.

struct ColorGradingUniforms
{
    float4 lift;        // additive offset for blacks (rgb; w unused)
    float4 gamma;       // midtone power curve per channel (1 = neutral)
    float4 gain;        // highlight scale per channel (1 = neutral)
    float temperature;  // white balance: negative = cooler (blue), positive = warmer (amber)
    float tint;         // white balance: negative = magenta, positive = green
    float saturation;   // global saturation (1 = neutral, 0 = greyscale, 2 = double)
    float contrast;     // pivot contrast about mid grey (0 = neutral, -1..1)
    float brightness;   // additive brightness offset (0 = neutral)
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<ColorGradingUniforms> postProcess;

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
    output.uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    return output;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    var color = sceneTexture.Sample(sceneSampler, input.uv).rgb;

    // White balance: temperature shifts red against blue (warm vs cool) and
    // tint shifts green against magenta. Both are simple complementary channel
    // gains applied across the frame.
    let temperature = clamp(postProcess.temperature, -1.0, 1.0);
    let tint = clamp(postProcess.tint, -1.0, 1.0);
    let warm = temperature * 0.18;
    color *= float3(
        1.0 + warm + tint * 0.10,
        1.0 - tint * 0.20,
        1.0 - warm + tint * 0.10);

    // Pivot contrast about mid grey (linear space), 0 = neutral.
    let contrast = clamp(postProcess.contrast, -1.0, 1.0);
    color = (color - float3(0.5)) * (1.0 + contrast) + float3(0.5);

    // Brightness is a simple additive offset in the linear HDR domain.
    color += float3(postProcess.brightness);

    // Rec.709 relative luminance for a luma-preserving saturation blend.
    let luma = dot(color, float3(0.2126, 0.7152, 0.0722));
    color = lerp(float3(luma), color, clamp(postProcess.saturation, 0.0, 2.0));

    // Unity-style lift / gamma / gain. Gain scales the highlights, lift offsets
    // the blacks, and gamma curves the mids (pow with sign preserved so dark or
    // out-of-range values don't produce NaNs).
    color = color * postProcess.gain.xyz + postProcess.lift.xyz;
    color = sign(color) * pow(abs(color), postProcess.gamma.xyz);

    return float4(color, 1.0);
}