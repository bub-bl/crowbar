#language slang 2026;
// Bloom prefilter + half-res extract: reads the linear HDR scene and writes the
// bright pixels (above a luminance threshold, with a smooth knee) to the finest
// bloom pyramid level (½ resolution). The 2x2 bilinear downsample is implied by
// sampling the scene texture. The uniform
// struct's fields (threshold, thresholdKnee, clamp) are packed by name from the
// Bloom component's [Property] values, per the shared post-process convention.

struct BloomPrefilterUniforms
{
    float threshold;       // luminance above which bloom starts
    float thresholdKnee;   // width of the soft knee (feather below threshold)
    float clamp_;          // upper bound on extracted brightness (firefly control)
    float pad;             // alignment
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<BloomPrefilterUniforms> postProcess;

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
    if (postProcess.clamp_ > 0.0)
        color = min(color, float3(postProcess.clamp_));
    let brightness = max(color.r, max(color.g, color.b));

    // Soft knee: below (threshold - knee) nothing blooms; ramp linearly across
    // the knee until it saturates at threshold. This denoises faint detail
    // (Unity's bloom uses the same prefilter) instead of a hard cut.
    let knee = max(postProcess.thresholdKnee, 1e-4);
    let soft = brightness - postProcess.threshold + knee;
    let softWeight = clamp(soft / (2.0 * knee), 0.0, 1.0);
    let hardWeight = max(brightness - postProcess.threshold, 0.0);
    let weight = max(hardWeight, softWeight * softWeight * knee) / max(brightness, 1e-4);

    return float4(color * weight, 1.0);
}
