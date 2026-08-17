#language slang 2026;
import Scene;

// One instanced billboard: world center, tint, shape/scale and an atlas region.
struct GizmoSpriteParams
{
    float4 center;        // xyz = world position
    float4 color;
    float4 scaleKind;     // x = half-size (world), y = kind (0 circle, 1 diamond, 2 ring, 3 icon, 4 square)
    float4 uvRect;        // atlas rectangle (u0, v0, u1, v1)
};

[[vk::binding(1, 0)]] StructuredBuffer<GizmoSpriteParams> sprites;
[[vk::binding(2, 0)]] Texture2D iconAtlas;
[[vk::binding(3, 0)]] SamplerState iconSampler;

struct SpriteInput
{
    [[vk::location(0)]] float2 corner : POSITION;
};

struct SpriteOutput
{
    float4 clip_position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
    [[vk::location(1)]] float4 color : TEXCOORD1;
    [[vk::location(2)]] float kind : TEXCOORD2;
    [[vk::location(3)]] float4 uvRect : TEXCOORD3;
};

[shader("vertex")]
SpriteOutput vs_main(SpriteInput input, uint instance : SV_InstanceID)
{
    let params = sprites[instance];

    // Camera basis from the view matrix: with the engine's storage layout the
    // first row of the view holds (right, up, forward) per component.
    let right = scene.view[0].xyz;
    let up = scene.view[1].xyz;

    SpriteOutput out;
    let offset = right * (input.corner.x * params.scaleKind.x) + up * (input.corner.y * params.scaleKind.x);
    let world_position = params.center.xyz + offset;
    out.clip_position = mul(mul(scene.proj, scene.view), float4(world_position, 1.0));
    out.uv = input.corner;
    out.color = params.color;
    out.kind = params.scaleKind.y;
    out.uvRect = params.uvRect;
    return out;
}

[shader("fragment")]
float4 fs_main(SpriteOutput input) : SV_Target
{
    // Filled square (scale handles).
    if (input.kind > 3.5)
    {
        let square = max(abs(input.uv.x), abs(input.uv.y));
        let alpha = 1.0 - smoothstep(0.45, 0.5, square);
        if (alpha <= 0.003)
        {
            discard;
        }
        return float4(input.color.rgb, input.color.a * alpha);
    }

    // Icon sprites use the supplied SVG atlas. The source SVGs are rasterized
    // white and tinted here, so light/entity colors remain dynamic.
    if (input.kind > 2.5)
    {
        let uv01 = input.uv * 0.5 + float2(0.5);
        // Raster images use a top-left origin; billboard UVs use a bottom-left
        // origin because corner.y=-1 is the bottom of the screen sprite.
        let atlasUv = float2(
            lerp(input.uvRect.x, input.uvRect.z, uv01.x),
            lerp(input.uvRect.w, input.uvRect.y, uv01.y));
        let sampled = iconAtlas.Sample(iconSampler, atlasUv);
        if (sampled.a <= 0.003)
        {
            discard;
        }
        return float4(sampled.rgb * input.color.rgb, sampled.a * input.color.a);
    }

    let distance = length(input.uv);
    let diamond = abs(input.uv.x) + abs(input.uv.y);

    var alpha = 0.0;
    if (input.kind < 0.5)
    {
        alpha = 1.0 - smoothstep(0.42, 0.5, distance);
    }
    else if (input.kind < 1.5)
    {
        alpha = 1.0 - smoothstep(0.42, 0.5, diamond);
    }
    else
    {
        alpha = (1.0 - smoothstep(0.45, 0.5, distance)) * (1.0 - smoothstep(0.32, 0.37, distance));
    }

    // Discard fully transparent pixels so the blend never darkens the scene
    // behind an invisible procedural sprite.
    if (alpha <= 0.003)
    {
        discard;
    }
    return float4(input.color.rgb, input.color.a * alpha);
}
