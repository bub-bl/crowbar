#language slang 2026;
// Radial display-referred chromatic aberration. Red shifts outward, blue
// shifts inward, and green remains centered. The displacement is in pixels.

struct ChromaticAberrationUniforms
{
    float intensity;
    float start;
    float2 viewportSize;
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<ChromaticAberrationUniforms> postProcess;

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
    let viewport = max(postProcess.viewportSize, float2(1.0));
    let aspect = viewport.x / viewport.y;
    let centered = (input.uv - float2(0.5)) * float2(aspect, 1.0);
    let cornerDistance = length(float2(0.5 * aspect, 0.5));
    let radius = length(centered) / max(cornerDistance, 1e-4);
    let direction = length(centered) > 1e-5 ? normalize(centered) : float2(0.0);
    let edgeWeight = smoothstep(postProcess.start, 1.0, radius);
    let offset = direction * edgeWeight * (6.0 * postProcess.intensity) / viewport;
    let center = sceneTexture.Sample(sceneSampler, input.uv).rgb;
    let red = sceneTexture.Sample(sceneSampler, input.uv + offset).r;
    let blue = sceneTexture.Sample(sceneSampler, input.uv - offset).b;
    return float4(red, center.g, blue, 1.0);
}
