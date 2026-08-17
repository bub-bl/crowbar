#language slang 2026;
import Color;
import Screen;

// Textured-quad renderer (Crowbar 2D UI backend).
//
// Images are packed into a single atlas texture and drawn as screen-space
// quads carrying a UV rectangle and a straight sRGB tint. One Draw call
// renders every image in the batch; the tint lets the same atlas entry be
// reused at different opacities/colors (icons, buttons, editor thumbnails).

[[vk::binding(0, 0)]] Texture2D image_atlas;
[[vk::binding(1, 0)]] SamplerState image_sampler;
[[vk::binding(2, 0)]] ConstantBuffer<float4> viewport; // xy = size, zw = 1/size

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
    [[vk::location(1)]] float4 color : TEXCOORD1;
};

[shader("vertex")]
VertexOutput vs_main([[vk::location(0)]] float2 position : POSITION, [[vk::location(1)]] float2 uv : TEXCOORD0,
                     [[vk::location(2)]] float4 color : COLOR)
{
    VertexOutput out;
    out.position = float4(Screen::PixelToClip(position, viewport), 0.0, 1.0);
    out.uv = uv;
    out.color = color;
    return out;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    let tex = image_atlas.Sample(image_sampler, input.uv);
    let rgb = Color::SrgbToLinear(tex.rgb) * Color::SrgbToLinear(input.color.rgb);
    return float4(rgb, tex.a * input.color.a);
}
