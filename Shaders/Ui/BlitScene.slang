#language slang 2026;
import Color;

// Scene blit: presents the post-processed display texture (linear HDR,
// Rgba16Float, alpha 1) to the 8-bit surface. Unlike the UI blit
// (Blit.slang), the scene texture already holds linear display-referred
// values, so nothing is un-premultiplied or sRGB-decoded here. The fragment
// instead encodes linear -> sRGB, because the surface swapchain is a plain
// 8-bit unorm format with no hardware sRGB store — this is the single display
// encode point of the 3D path (post-process shaders write linear into the
// display texture).

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[[vk::binding(0, 0)]] Texture2D scene_tex;
[[vk::binding(1, 0)]] SamplerState scene_sampler;

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
    let encoded = Color::LinearToSrgb(clamp(color.rgb, float3(0.0), float3(1.0)));
    return float4(encoded, color.a);
}