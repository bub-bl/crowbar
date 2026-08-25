#language slang 2026;
import Color;

// Scene blit for either surface format. The display texture is linear HDR;
// plain unorm surfaces use the manual encode while sRGB surfaces encode in
// hardware. `surface.colorSpace.x` is 1 for an sRGB surface.

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[[vk::binding(0, 0)]] Texture2D scene_tex;
[[vk::binding(1, 0)]] SamplerState scene_sampler;

struct SurfaceColorSpace
{
    float4 colorSpace;
};
[[vk::binding(2, 0)]] ConstantBuffer<SurfaceColorSpace> surface;

[shader("vertex")]
VertexOutput vs_main([[vk::location(0)]] float2 position : POSITION, [[vk::location(1)]] float2 uv : TEXCOORD0)
{
    VertexOutput output;
    output.position = float4(position, 0.0, 1.0);
    output.uv = uv;
    return output;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    let color = scene_tex.Sample(scene_sampler, input.uv);
    let rgb = clamp(color.rgb, float3(0.0), float3(1.0));
    let encoded = surface.colorSpace.x > 0.5 ? rgb : Color::LinearToSrgb(rgb);
    return float4(encoded, color.a);
}