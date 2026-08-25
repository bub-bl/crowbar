#language slang 2026;
import Color;

// Selection outline pass: replaces the plain scene blit when an entity is
// selected. Samples the finished scene texture and the selection mask, then
// dilates the mask with a tent-weighted disk to tint a soft band just outside
// the silhouette — a real contour, independent of the mesh's own geometry
// (no inverted-hull scaling, which breaks at silhouettes and thin features).

struct OutlineParams
{
    float4 color;       // rgb = outline color, a = opacity
    float2 texel_size;  // 1 / viewport width, 1 / viewport height
    float thickness;    // outline radius in pixels
    float pad;
};

struct SurfaceColorSpace
{
    float4 colorSpace;
};

[[vk::binding(0, 0)]] Texture2D scene_tex;
[[vk::binding(1, 0)]] Texture2D mask_tex;
[[vk::binding(2, 0)]] SamplerState samp;
[[vk::binding(3, 0)]] ConstantBuffer<OutlineParams> params;
[[vk::binding(4, 0)]] ConstantBuffer<SurfaceColorSpace> surface;

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

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
    let tex = scene_tex.Sample(samp, input.uv);
    let alpha = tex.a;
    let rgb = clamp(tex.rgb, float3(0.0), float3(1.0));

    let center = mask_tex.Sample(samp, input.uv).r;

    // Inside the selected object's silhouette, keep the scene unchanged apart
    // from the required display encode.
    if (center > 0.5)
    {
        let outputRgb = surface.colorSpace.x > 0.5 ? rgb : Color::LinearToSrgb(rgb);
        return float4(outputRgb, alpha);
    }

    // Outside: weight the mask over a disk of radius `thickness`. Right at
    // the silhouette roughly half the disk overlaps the object, so the sum
    // peaks there and falls off with distance — a band with soft inner and
    // outer edges instead of a hard 1px fringe.
    let radius = int(clamp(round(params.thickness), 1.0, 12.0));
    var outside = 0.0;
    var total = 0.0;
    for (int dy = -radius; dy <= radius; dy++)
    {
        for (int dx = -radius; dx <= radius; dx++)
        {
            let distance = sqrt(float(dx * dx + dy * dy));
            if (distance > float(radius))
            {
                continue;
            }
            let weight = 1.0 - distance / (float(radius) + 1.0);
            total += weight;
            outside += weight * mask_tex.Sample(samp,
                input.uv + float2(float(dx), float(dy)) * params.texel_size).r;
        }
    }

    let edge = clamp(outside * (2.0 / total), 0.0, 1.0);
    let blend = smoothstep(0.2, 0.8, edge) * params.color.a;
    // Mix in linear space, then apply the same display encode as the plain
    // scene blit. The alpha is the scene's own, so translucent pixels (grid
    // lines) composite over the cleared surface exactly like the blit.
    let mixed = lerp(rgb, clamp(params.color.rgb, float3(0.0), float3(1.0)), blend);
    let outputRgb = surface.colorSpace.x > 0.5 ? mixed : Color::LinearToSrgb(mixed);
    return float4(outputRgb, alpha);
}
