#language slang 2026;
import Color;
import Msdf;
import Screen;

// MSDF glyph shadow renderer (Crowbar 2D UI backend).
//
// A text shadow reuses the glyph atlas: the same glyph quad is drawn once at an
// offset with the shadow color, but with a wider smoothstep band (softness), so
// the multi-channel distance field falls off across the blur radius instead of
// the fixed 1px antialiasing band. The distance is the median of the three MSDF
// channels, matching Glyph.slang. One Draw renders every glyph shadow in the
// batch.

[[vk::binding(0, 0)]] Texture2D glyph_atlas;
[[vk::binding(1, 0)]] SamplerState glyph_sampler;
[[vk::binding(2, 0)]] ConstantBuffer<float4> viewport; // xy = size, zw = 1/size

// Must match GlyphRasterizer.Spread * Scale (the atlas is rasterized at 2×,
// so distances come back in grid units).
static const float SPREAD = 16.0;

// The vertex input must be a struct, not entry-point parameters: slangc
// reorders parameters and reassigns their @location values, while struct
// fields keep the declared [[vk::location]] tags. slangc does not honor
// [[vk::location]] on vertex-input structs either — it reorders the fields by
// type and renumbers the @location tags (see #5633/#9396) — so the emitted
// WGSL only matches the engine's vertex layout when the declared order equals
// slangc's internal type order. For the type set (float2, float, float2,
// float4) that order is exactly [position, softness, uv, color]; Renderer2D
// maps its vertex buffer attributes onto those locations. softness is constant
// across a quad, so it needs no flat interpolation.
struct GlyphVertexInput
{
    [[vk::location(0)]] float2 position : POSITION;
    [[vk::location(1)]] float softness : TEXCOORD1;
    [[vk::location(2)]] float2 uv : TEXCOORD0;
    [[vk::location(3)]] float4 color : COLOR;
};

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
    [[vk::location(1)]] float4 color : TEXCOORD1;
    [[vk::location(2)]] float softness : TEXCOORD2;
};

[shader("vertex")]
VertexOutput vs_main(GlyphVertexInput input)
{
    VertexOutput out;
    out.position = float4(Screen::PixelToClip(input.position, viewport), 0.0, 1.0);
    out.uv = input.uv;
    out.color = input.color;
    out.softness = input.softness;
    return out;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    let msd = glyph_atlas.Sample(glyph_sampler, input.uv).rgb;
    let distance = Msdf::ScreenPixelRange(glyph_atlas, input.uv, SPREAD) * (Msdf::Median(msd.r, msd.g, msd.b) - 0.5);
    let coverage = smoothstep(-max(input.softness, 1.0), max(input.softness, 1.0), distance);
    return float4(Color::SrgbToLinear(input.color.rgb), coverage * input.color.a);
}
