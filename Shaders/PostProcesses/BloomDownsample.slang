#language slang 2026;
// Bloom pyramid downsample: averages a 2x2 block of the finer level into the
// next coarser level with a single bilinear sample placed at the 2x2 centroid.
// The bloom component passes the finer level's texel size (inverse) so the tap
// lands exactly between the four surrounding pixels. Convention shared by every
// post-process shader: slot 0 texture, slot 1 sampler, slot 2 uniform buffer.

struct BloomDownsampleUniforms
{
    float2 invTexel; // 1 / (finer level width, height); half that offsets the sample to the 2x2 center
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<BloomDownsampleUniforms> postProcess;

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
    // Sample at the center of the 2x2 block (the bilinear filter averages the
    // four surrounding texels). edx/edy offset the coarser grid onto the finer
    // texel lattice so adjacent output pixels don't overlap.
    let offset = postProcess.invTexel * float2(0.5);
    let c = sceneTexture.Sample(sceneSampler, input.uv - offset);
    let e = sceneTexture.Sample(sceneSampler, float2(input.uv.x + offset.x, input.uv.y - offset.y));
    let n = sceneTexture.Sample(sceneSampler, float2(input.uv.x - offset.x, input.uv.y + offset.y));
    let w = sceneTexture.Sample(sceneSampler, input.uv + offset);
    let average = (c + e + n + w) * 0.25;
    return float4(average.rgb, 1.0);
}