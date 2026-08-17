// Blit pass for the Renderer2D offscreen target (the GPU-rendered UI layer).
//
// The Renderer2D draws into an Rgba8Unorm target whose shaders already write
// linear color + straight (non-premultiplied) alpha. Unlike Ui.slang (which
// un-premultiplies and decodes the sRGB raster), this shader is a plain
// passthrough: it forwards the linear straight-alpha texel so the SrcAlpha
// blend below reproduces browser-style compositing on the surface.

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
    return ui_texture.Sample(ui_sampler, input.uv);
}
