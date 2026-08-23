#language slang 2026;
import Camera;
import Environment;

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 clip : TEXCOORD0;
};

[shader("vertex")]
VertexOutput vs_main(uint vertexId : SV_VertexID)
{
    let clip = float2(float((vertexId << 1u) & 2u), float(vertexId & 2u)) * 2.0 - 1.0;
    VertexOutput output;
    output.position = float4(clip, 1.0, 1.0);
    output.clip = clip;
    return output;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    let viewDirection = normalize(float3(
        input.clip.x / camera.proj[0][0],
        input.clip.y / camera.proj[1][1],
        1.0));
    let worldDirection = normalize(mul(transpose((float3x3)camera.view), viewDirection));
    return float4(EnvironmentLighting::SampleSky(worldDirection), 1.0);
}
