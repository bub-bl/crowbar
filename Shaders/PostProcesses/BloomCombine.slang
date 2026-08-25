#language slang 2026;
// Bloom final combine: adds the re-composed glow (finest pyramid level) back
// onto the original linear HDR scene. Two inputs: the scene at slot 0 and the
// glow at slot 3 (bound by the renderer's secondary-input path), one sampler at
// slot 1 and a second at slot 4 for the glow. The output is written to the
// current chain target (the next post-process or the display texture), so bloom
// composes with tonemapping and the rest of the chain. Convention: one uniform
// buffer in group 0 packed by name.

struct BloomCombineUniforms
{
    float intensity; // overall glow amount
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<BloomCombineUniforms> postProcess;
[[vk::binding(3, 0)]] Texture2D glowTexture;
[[vk::binding(4, 0)]] SamplerState glowSampler;

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
    var scene = sceneTexture.Sample(sceneSampler, input.uv).rgb;
    var glow = glowTexture.Sample(glowSampler, input.uv).rgb;
    return float4(scene + glow * postProcess.intensity, 1.0);
}