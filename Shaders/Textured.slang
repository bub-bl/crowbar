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

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    let tex = image_atlas.Sample(image_sampler, input.uv);
    let rgb = srgbToLinear(tex.rgb) * srgbToLinear(input.color.rgb);
    return float4(rgb, tex.a * input.color.a);
}
