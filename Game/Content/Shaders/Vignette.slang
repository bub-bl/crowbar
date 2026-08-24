#language slang 2026;
// Game-shipped post-process example (Content/Shaders): darkens the frame
// corners. The uniform struct's fields (intensity, radius) are packed by name
// from the Vignette component's [Property] values — the convention shared by
// every post-process shader: sample the previous chain texture at slot 0,
// declare a sampler at slot 1, and one uniform buffer in group 0 whose fields
// are packed by name (trailing underscores ignored, case ignored).

struct VignetteUniforms
{
    float intensity; // corner darkening strength (0 = none)
    float radius;    // normalized distance from the center where the fade starts
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<VignetteUniforms> postProcess;

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
    let centered = input.uv - float2(0.5);
    let dist = length(centered);
    let falloff = smoothstep(postProcess.radius, postProcess.radius + 0.4, dist);
    let shade = 1.0 - falloff * postProcess.intensity;
    var color = sceneTexture.Sample(sceneSampler, input.uv).rgb * shade;
    return float4(color, 1.0);
}
