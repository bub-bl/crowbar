#language slang 2026;
import Color;

// Scene-raster blit: presents the offscreen UI raster to the surface. The
// texture stores the rasterizer's premultiplied sRGB-encoded RGBA (uploaded
// raw from the bitmap, no CPU conversion). Linear filtering on premultiplied
// values is correct (no fringe on soft edges); un-premultiply and decode sRGB
// here so the SrcAlpha blend below reproduces browser-style straight-alpha
// compositing, while opaque pixels round-trip exactly.

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[[vk::binding(0, 0)]] Texture2D ui_texture;
[[vk::binding(1, 0)]] SamplerState ui_sampler;

[shader("vertex")]
VertexOutput vs_main([[vk::location(0)]] float2 position : POSITION, [[vk::location(1)]] float2 uv : TEXCOORD0)
{
    VertexOutput o;
    o.position = float4(position, 0.0, 1.0);
    o.uv = uv;
    return o;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    let tex = ui_texture.Sample(ui_sampler, input.uv);
    let a = tex.a;
    var rgb = float3(0.0);
    if (a > 0.0)
    {
        rgb = Color::SrgbToLinear(clamp(tex.rgb / a, float3(0.0), float3(1.0)));
    }
    return float4(rgb, a);
}
