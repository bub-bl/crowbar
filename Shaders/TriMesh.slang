// Tessellated-triangle renderer (Crowbar 2D UI backend).
//
// Polygons (and, later, SVG paths, glyph quads and image quads) are
// tessellated on the CPU into screen-space triangles carrying a straight sRGB
// color. This shader projects the already-transformed positions and decodes
// the color; it is the fallback path for geometry the SDF shader cannot
// express, batched into a single Draw call per frame.

[[vk::binding(0, 0)]] ConstantBuffer<float4> viewport; // xy = size, zw = 1/size

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float4 color : TEXCOORD0;
};

[shader("vertex")]
VertexOutput vs_main([[vk::location(0)]] float2 position : POSITION, [[vk::location(1)]] float4 color : COLOR)
{
    VertexOutput out;
    out.position = float4(position.x * viewport.z * 2.0 - 1.0, 1.0 - position.y * viewport.w * 2.0, 0.0, 1.0);
    out.color = color;
    return out;
}

float3 srgbToLinear(float3 c)
{
    let lo = c / 12.92;
    let hi = pow((c + 0.055) / 1.055, float3(2.4));
    return select(c <= float3(0.04045), lo, hi);
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    return float4(srgbToLinear(input.color.rgb), input.color.a);
}
