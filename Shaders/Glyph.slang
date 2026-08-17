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
    out.position = float4(position.x * viewport.z * 2.0 - 1.0, 1.0 - position.y * viewport.w * 2.0, 0.0, 1.0);
    out.uv = uv;
    out.color = color;
    return out;
}

float3 srgbToLinear(float3 c)
{
    let lo = c / 12.92;
    let hi = pow((c + 0.055) / 1.055, float3(2.4));
    return select(c <= float3(0.04045), lo, hi);
}

float median(float r, float g, float b)
{
    return max(min(r, g), min(max(r, g), b));
}

// Reference MSDF antialiasing from msdfgen. Derive the screen-space range
// from the UV gradient, rather than from the reconstructed distance: the
// median has discontinuities at edge-color transitions, so fwidth(distance)
// can turn a valid contour into a broad horizontal smear.
float screenPxRange(float2 uv)
{
    uint width, height;
    glyph_atlas.GetDimensions(width, height);
    let unitRange = float2(SPREAD) / float2(width, height);
    let dx = ddx(uv);
    let dy = ddy(uv);
    let screenTexSize = rsqrt(dx * dx + dy * dy);
    return max(0.5 * dot(unitRange, screenTexSize), 1.0);
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    let msd = glyph_atlas.Sample(glyph_sampler, input.uv).rgb;
    let coverage = clamp(screenPxRange(input.uv) * (median(msd.r, msd.g, msd.b) - 0.5) + 0.5, 0.0, 1.0);
    return float4(srgbToLinear(input.color.rgb), coverage * input.color.a);
}
