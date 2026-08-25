#language slang 2026;
// Bloom upsample blur: walks the pyramid back toward full resolution. It samples
// the coarser (blurred) level with a Gaussian-weighted 3x3 tap and writes the
// result into the finer level with additive blending. The bloom component runs
// this from the coarsest level down to the finest, so every scale is preserved
// while broad halos accumulate. Scatter controls the coarse-level contribution.
// Convention: slot 0 texture, slot 1 sampler, slot 2 uniform buffer.

struct BloomUpsampleUniforms
{
    float2 invTexel;
    float scatter;
    float pad;
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<BloomUpsampleUniforms> postProcess;

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
    let d = postProcess.invTexel;
    var acc = sceneTexture.Sample(sceneSampler, input.uv).rgb * 4.0;
    acc += sceneTexture.Sample(sceneSampler, input.uv + float2(d.x, 0.0)).rgb * 2.0;
    acc += sceneTexture.Sample(sceneSampler, input.uv - float2(d.x, 0.0)).rgb * 2.0;
    acc += sceneTexture.Sample(sceneSampler, input.uv + float2(0.0, d.y)).rgb * 2.0;
    acc += sceneTexture.Sample(sceneSampler, input.uv - float2(0.0, d.y)).rgb * 2.0;
    acc += sceneTexture.Sample(sceneSampler, input.uv + d).rgb;
    acc += sceneTexture.Sample(sceneSampler, input.uv - d).rgb;
    acc += sceneTexture.Sample(sceneSampler, input.uv + float2(d.x, -d.y)).rgb;
    acc += sceneTexture.Sample(sceneSampler, input.uv + float2(-d.x, d.y)).rgb;
    return float4(acc * (postProcess.scatter / 16.0), 0.0);
}
