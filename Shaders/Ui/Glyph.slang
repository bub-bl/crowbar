#language slang 2026;
import Color;
import Msdf;
import Screen;

// MSDF glyph renderer (Crowbar 2D UI backend).
//
// Text is rasterized once into a multi-channel signed distance field (MSDF)
// packed in the glyph atlas: RGB hold the three channel distances and alpha
// retains the true signed distance for CPU-side correction/debugging. The
// fragment shader reconstructs the MSDF distance as the median of the three
// channels (Chlumský's msdfgen), which stays exact at sharp corners under
// bilinear filtering — a single-channel SDF would round them. Each glyph is a
// screen-space quad carrying the atlas UV and a straight sRGB color, so text
// stays crisp at any scale with no per-size rasterization.

[[vk::binding(0, 0)]] Texture2D glyph_atlas;
[[vk::binding(1, 0)]] SamplerState glyph_sampler;
[[vk::binding(2, 0)]] ConstantBuffer<float4> viewport; // xy = size, zw = 1/size

// Must match GlyphRasterizer.Spread * GlyphRasterizer.Scale. This is the
// representable distance range in atlas texels.
static const float SPREAD = 16.0;

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
    let msd = glyph_atlas.Sample(glyph_sampler, input.uv).rgb;
    let coverage = clamp(Msdf::ScreenPixelRange(glyph_atlas, input.uv, SPREAD) * (Msdf::Median(msd.r, msd.g, msd.b) - 0.5) + 0.5, 0.0, 1.0);
    return float4(Color::SrgbToLinear(input.color.rgb), coverage * input.color.a);
}
