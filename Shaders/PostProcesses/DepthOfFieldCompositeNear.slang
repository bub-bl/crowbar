#language slang 2026;
// Depth-of-field near composite: draws the near-blurred layer over the already
// far-composited image. The near blur texture carries a coverage alpha (how far
// its bokeh disk reaches), which replaces the background where near foreground
// bokeh is present while leaving the rest untouched.

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(3, 0)]] Texture2D blurTexture;
[[vk::binding(4, 0)]] SamplerState blurSampler;

struct VertexOutput { float4 position : SV_Position; [[vk::location(0)]] float2 uv : TEXCOORD0; };
[shader("vertex")]
VertexOutput vs_main(uint vertexId : SV_VertexID)
{
    let clip = float2(float((vertexId << 1u) & 2u), float(vertexId & 2u)) * 2.0 - 1.0;
    VertexOutput output; output.position = float4(clip, 0.0, 1.0);
    output.uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5); return output;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    let farLayer = sceneTexture.Sample(sceneSampler, input.uv).rgb;
    let nearLayer = blurTexture.Sample(blurSampler, input.uv);
    return float4(lerp(farLayer, nearLayer.rgb, nearLayer.a), 1.0);
}