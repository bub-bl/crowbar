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
    float2 viewportSize;
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
    // Distance from the center normalized by the half-diagonal, so the frame
    // corners sit at 1.0. The radius is inverted: the darkening covers radius
    // of the half-diagonal from the corners inward, so a higher value means a
    // stronger vignette (0 = none, 1 = fades in from the center). The fade
    // edge is clamped below 1 so smoothstep never degenerates (radius 0 or 1
    // used to produce no effect at all).
    let aspect = postProcess.viewportSize.x / max(postProcess.viewportSize.y, 1.0);
    let centered = (input.uv - float2(0.5)) * float2(aspect, 1.0);
    let cornerDistance = length(float2(0.5 * aspect, 0.5));
    let dist = length(centered) / max(cornerDistance, 1e-4);
    let edge = clamp(1.0 - postProcess.radius, 0.0, 0.999);
    let falloff = smoothstep(edge, 1.0, dist);
    let shade = max(0.0, 1.0 - falloff * postProcess.intensity);
    var color = sceneTexture.Sample(sceneSampler, input.uv).rgb * shade;
    return float4(color, 1.0);
}
