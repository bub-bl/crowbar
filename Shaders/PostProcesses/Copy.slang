#language slang 2026;
// Engine identity pass: copies the previous chain texture to the next target
// unchanged. The renderer runs this when the world has no PostProcess
// component (or the camera disabled post-processing) so the scene is
// presented exactly as rendered — the engine never applies a tonemapping on
// its own. The default look is authored content: the demo level ships a
// Tonemapping component on its camera.
//
// Same fullscreen-pass convention as every post-process shader: sample the
// previous chain texture at slot 0, declare a sampler at slot 1, and write
// to the current chain render target. No uniform buffer — this pass carries
// no settings.

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;

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
    return float4(sceneTexture.Sample(sceneSampler, input.uv).rgb, 1.0);
}
