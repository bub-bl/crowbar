#language slang 2026;
// Display-referred monochrome film grain. A new deterministic noise field is
// selected at 24 Hz. Two independent uniform samples form a centered triangular
// distribution, avoiding the harsh extremes of single-sample white noise.

struct FilmGrainUniforms
{
    float intensity;
    float response;
    float time;
    float pad;
    float2 viewportSize;
    float2 padding;
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<FilmGrainUniforms> postProcess;

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

static float Hash(float3 value)
{
    value = frac(value * 0.1031);
    value += dot(value, value.yzx + 33.33);
    return frac((value.x + value.y) * value.z);
}

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
    let color = sceneTexture.Sample(sceneSampler, input.uv).rgb;
    let pixel = floor(input.uv * max(postProcess.viewportSize, float2(1.0)));
    let frame = floor(postProcess.time * 24.0);
    let noise = Hash(float3(pixel, frame)) + Hash(float3(pixel.yx + 17.0, frame + 11.0)) - 1.0;
    let luminance = dot(color, float3(0.2126, 0.7152, 0.0722));
    let highlightSuppression = 1.0 - smoothstep(0.15, 1.0, luminance);
    let response = lerp(1.0, highlightSuppression, postProcess.response);
    let grain = noise * response * postProcess.intensity * 0.08;
    return float4(clamp(color + float3(grain), float3(0.0), float3(1.0)), 1.0);
}
