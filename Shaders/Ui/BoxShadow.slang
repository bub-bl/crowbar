#language slang 2026;
import Color;
import Sdf;
import Screen;

// Instanced soft shadow renderer (Crowbar 2D UI backend).
//
// One instanced unit quad is drawn per shadow. The fragment evaluates a
// rounded-rect signed distance field and produces a soft falloff across the
// blur radius, mirroring the existing Decorations shader formula. Outer shadows
// clip the panel's own border box out of themselves; inner shadows are masked
// to the box interior. Up to four screen-space clips are applied, so shadows
// respect the same clip stack as the SDF shapes.

struct ShadowInstance
{
    float4 m0;       // transform row: screen.x = m0.x*lx + m0.y*ly + m0.z
    float4 m1;       // transform row: screen.y = m1.x*lx + m1.y*ly + m1.z
    float4 quad;     // rasterization bounds (local): xy = top-left, zw = size
    float4 shape;    // shadow shape (local): xy = top-left, zw = size
    float4 box;      // panel border box (local): xy = top-left, zw = size
    float4 radii;    // x = shape corner radius, y = box corner radius, z = blur, w unused
    float4 color;    // straight sRGB RGBA
    float4 flags;    // x = kind (0 outer, 1 inner), y = clip count
    float4 clip0;    // screen-space clip rects (xy = top-left, zw = size)
    float4 clip1;
    float4 clip2;
    float4 clip3;
    float4 clipRadii; // clip corner radii (px)
};

[[vk::binding(0, 0)]] StructuredBuffer<ShadowInstance> instances;
[[vk::binding(1, 0)]] ConstantBuffer<float4> viewport; // xy = size, zw = 1/size

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 local : TEXCOORD0;
    [[vk::location(1)]] nointerpolation uint instance : TEXCOORD1;
};

[shader("vertex")]
VertexOutput vs_main([[vk::location(0)]] float2 position : POSITION, [[vk::location(1)]] float2 uv : TEXCOORD0,
                     uint instanceIndex : SV_InstanceID)
{
    VertexOutput out;
    let inst = instances[instanceIndex];
    let local = inst.quad.xy + uv * inst.quad.zw;
    let screen = float2(
        inst.m0.x * local.x + inst.m0.y * local.y + inst.m0.z,
        inst.m1.x * local.x + inst.m1.y * local.y + inst.m1.z);
    out.position = float4(Screen::PixelToClip(screen, viewport), 0.0, 1.0);
    out.local = local;
    out.instance = instanceIndex;
    return out;
}

float4 clipRect(ShadowInstance inst, uint i)
{
    var c = inst.clip0;
    if (i == 1u) { c = inst.clip1; }
    else if (i == 2u) { c = inst.clip2; }
    else if (i == 3u) { c = inst.clip3; }
    return c;
}

float clipRadius(ShadowInstance inst, uint i)
{
    var r = inst.clipRadii.x;
    if (i == 1u) { r = inst.clipRadii.y; }
    else if (i == 2u) { r = inst.clipRadii.z; }
    else if (i == 3u) { r = inst.clipRadii.w; }
    return r;
}

float clipMask(ShadowInstance inst, float2 frag)
{
    var mask = 1.0;
    let count = uint(inst.flags.y);
    if (count > 0u) { mask *= 1.0 - smoothstep(-1.0, 0.0, Sdf::RoundedRect(frag, clipRect(inst, 0u), clipRadius(inst, 0u))); }
    if (count > 1u) { mask *= 1.0 - smoothstep(-1.0, 0.0, Sdf::RoundedRect(frag, clipRect(inst, 1u), clipRadius(inst, 1u))); }
    if (count > 2u) { mask *= 1.0 - smoothstep(-1.0, 0.0, Sdf::RoundedRect(frag, clipRect(inst, 2u), clipRadius(inst, 2u))); }
    if (count > 3u) { mask *= 1.0 - smoothstep(-1.0, 0.0, Sdf::RoundedRect(frag, clipRect(inst, 3u), clipRadius(inst, 3u))); }
    return mask;
}

struct FragmentInput
{
    [[vk::location(0)]] float2 local : TEXCOORD0;
    [[vk::location(1)]] nointerpolation uint instance : TEXCOORD1;
};

[shader("fragment")]
float4 fs_main(float4 frag : SV_Position, FragmentInput input) : SV_Target
{
    let inst = instances[input.instance];
    let sdShape = Sdf::RoundedRect(input.local, inst.shape, inst.radii.x);
    let blur = max(inst.radii.z, 1.0);
    let sdBox = Sdf::RoundedRect(input.local, inst.box, inst.radii.y);
    let boxMask = 1.0 - smoothstep(-1.0, 0.0, sdBox);

    var alpha = 0.0;
    if (inst.flags.x < 0.5)
    {
        // Outer shadow: soft falloff outside the shape, with the box cut out.
        alpha = clamp(0.5 - sdShape / blur, 0.0, 1.0) * (1.0 - boxMask);
    }
    else
    {
        // Inner shadow: soft falloff inside the eroded shape, masked to the box.
        alpha = clamp(0.5 + sdShape / blur, 0.0, 1.0) * boxMask;
    }

    alpha *= clipMask(inst, frag.xy);
    return float4(Color::SrgbToLinear(inst.color.rgb), inst.color.a * clamp(alpha, 0.0, 1.0));
}
