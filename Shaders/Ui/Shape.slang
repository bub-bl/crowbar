#language slang 2026;
import Color;
import Sdf;
import Screen;

// Batched SDF shape renderer (Crowbar 2D UI backend).
//
// One instanced unit quad is drawn per shape. The fragment shader evaluates
// the shape as a signed-distance field, paints it with a solid color or an
// inline gradient (up to four stops baked into the instance), and multiplies
// through up to four stacked screen-space clips. A single DrawInstanced call
// therefore covers every rect / rounded-rect / circle / ellipse / line in the
// batch, which is what keeps the draw-call count independent of UI complexity.

struct SdfInstance
{
    float4 rect;       // local bounding box: xy = top-left, zw = size
    float4 m0;         // transform row: screen.x = m0.x*lx + m0.y*ly + m0.z
    float4 m1;         // transform row: screen.y = m1.x*lx + m1.y*ly + m1.z
    float4 color;      // straight sRGB RGBA (used when the gradient kind is 0)
    float4 params;     // x = corner radius, y = stroke width (0 = fill), z = border style, w = dash length
    float4 grad0;      // linear: start (local); radial: center (local); line: start
    float4 grad1;      // linear: end (local); radial: x = radius, y = focal; line: end
    float4 stop0;      // gradient stop colors (straight sRGB)
    float4 stop1;
    float4 stop2;
    float4 stop3;
    float4 offsets;    // gradient stop offsets (0..1, ascending)
    float4 clip0;      // screen-space clip rects (xy = top-left, zw = size)
    float4 clip1;
    float4 clip2;
    float4 clip3;
    float4 clipRadii;  // clip corner radii (px)
    float4 flags;      // x = shape kind, y = gradient kind, z = clip count, w = stop count
};

[[vk::binding(0, 0)]] StructuredBuffer<SdfInstance> instances;
[[vk::binding(1, 0)]] ConstantBuffer<float4> viewport; // xy = size, zw = 1/size

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 local : TEXCOORD0;
    // This wgpu build does not expose instance_index in the fragment stage, so
    // it is forwarded from the vertex shader as a flat varying.
    [[vk::location(1)]] nointerpolation uint instance : TEXCOORD1;
};

[shader("vertex")]
VertexOutput vs_main([[vk::location(0)]] float2 position : POSITION, [[vk::location(1)]] float2 uv : TEXCOORD0,
                     uint instanceIndex : SV_InstanceID)
{
    VertexOutput out;
    let inst = instances[instanceIndex];
    let local = inst.rect.xy + uv * inst.rect.zw;
    let screen = float2(
        inst.m0.x * local.x + inst.m0.y * local.y + inst.m0.z,
        inst.m1.x * local.x + inst.m1.y * local.y + inst.m1.z);
    out.position = float4(Screen::PixelToClip(screen, viewport), 0.0, 1.0);
    out.local = local;
    out.instance = instanceIndex;
    return out;
}

// The gradient stops are indexed with constant indices only (naga rejects
// dynamic indices into struct-member arrays); the count gates each step.
float4 stopColor(SdfInstance inst, uint i)
{
    var c = inst.stop0;
    if (i == 1u) { c = inst.stop1; }
    else if (i == 2u) { c = inst.stop2; }
    else if (i == 3u) { c = inst.stop3; }
    return c;
}

float stopOffset(SdfInstance inst, uint i)
{
    var o = inst.offsets.x;
    if (i == 1u) { o = inst.offsets.y; }
    else if (i == 2u) { o = inst.offsets.z; }
    else if (i == 3u) { o = inst.offsets.w; }
    return o;
}

float4 evalGradient(SdfInstance inst, float2 local)
{
    let kind = uint(inst.flags.y);
    var t = 0.0;
    if (kind == 1u)
    {
        let d = inst.grad1.xy - inst.grad0.xy;
        let denom = dot(d, d);
        t = select(denom > 0.0, clamp(dot(local - inst.grad0.xy, d) / denom, 0.0, 1.0), 0.0);
    }
    else if (kind == 2u)
    {
        let r = max(inst.grad1.x, 0.0001);
        t = clamp(length(local - inst.grad0.xy) / r, 0.0, 1.0);
    }
    let count = clamp(uint(inst.flags.w), 1u, 4u);
    var color = stopColor(inst, 0u);
    var prevOff = stopOffset(inst, 0u);
    var prevCol = color;
    for (uint i = 1; i < 4; i++)
    {
        if (i < count)
        {
            let off = stopOffset(inst, i);
            let col = stopColor(inst, i);
            if (t >= prevOff && t <= off)
            {
                let span = max(off - prevOff, 0.0001);
                color = lerp(prevCol, col, clamp((t - prevOff) / span, 0.0, 1.0));
            }
            prevOff = off;
            prevCol = col;
        }
    }
    return color;
}

float4 clipRect(SdfInstance inst, uint i)
{
    var c = inst.clip0;
    if (i == 1u) { c = inst.clip1; }
    else if (i == 2u) { c = inst.clip2; }
    else if (i == 3u) { c = inst.clip3; }
    return c;
}

float clipRadius(SdfInstance inst, uint i)
{
    var r = inst.clipRadii.x;
    if (i == 1u) { r = inst.clipRadii.y; }
    else if (i == 2u) { r = inst.clipRadii.z; }
    else if (i == 3u) { r = inst.clipRadii.w; }
    return r;
}

// Product of the (up to four) screen-space clip masks, each a rounded rect.
float clipMask(SdfInstance inst, float2 frag)
{
    var mask = 1.0;
    let count = uint(inst.flags.z);
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
    let kind = uint(inst.flags.x);

    var sd = 0.0;
    if (kind == 3u)
    {
        // Line segment (round caps fall out of the clamped projection).
        sd = Sdf::Segment(input.local, inst.grad0.xy, inst.grad1.xy) - max(inst.params.y * 0.5, 0.0);
    }
    else if (kind == 2u)
    {
        sd = Sdf::Ellipse(input.local, inst.rect);
    }
    else
    {
        sd = Sdf::RoundedRect(input.local, inst.rect, inst.params.x);
    }

    let strokeWidth = inst.params.y;
    let style = uint(inst.params.z);
    let dashLen = inst.params.w;

    // A stroke is the ring between |sd| <= width/2; a fill is the interior.
    // Dashed/dotted borders modulate the ring along the outline; double draws
    // two concentric rings.
    var coverage = 0.0;
    if (strokeWidth > 0.0 && kind != 3u)
    {
        if (style == 3u)
        {
            // Double: two rings at +/- width/3, each width/3 thick.
            let third = strokeWidth / 3.0;
            let outer = 1.0 - smoothstep(-1.0, 0.0, abs(sd - third) - third * 0.5);
            let inner = 1.0 - smoothstep(-1.0, 0.0, abs(sd + third) - third * 0.5);
            coverage = max(outer, inner);
        }
        else if (style == 1u || style == 2u)
        {
            let dist = select(kind == 2u,
                Sdf::EllipseOutlineDistance(input.local, inst.rect),
                Sdf::RectOutlineDistance(input.local, inst.rect, inst.params.x));
            if (style == 1u)
            {
                coverage = (1.0 - smoothstep(-1.0, 0.0, abs(sd) - strokeWidth * 0.5)) * Sdf::Dash(dist, style, dashLen);
            }
            else
            {
                // Dotted: round dots centred on the boundary.
                let period = max(dashLen * 3.0, 1.0);
                let x = dist - period * floor(dist / period);
                let along = min(x, period - x);
                let rdot = max(dashLen * 0.5, 0.5);
                let d2 = sqrt(sd * sd + along * along);
                coverage = 1.0 - smoothstep(-1.0, 1.0, d2 - rdot);
            }
        }
        else
        {
            coverage = 1.0 - smoothstep(-1.0, 0.0, abs(sd) - strokeWidth * 0.5);
        }
    }
    else if (strokeWidth > 0.0)
    {
        // Line stroke (round caps).
        if (style == 1u || style == 2u)
        {
            let dist = Sdf::LineOutlineDistance(input.local, inst.grad0.xy, inst.grad1.xy);
            if (style == 1u)
            {
                coverage = (1.0 - smoothstep(-1.0, 0.0, sd)) * Sdf::Dash(dist, style, dashLen);
            }
            else
            {
                let period = max(dashLen * 3.0, 1.0);
                let x = dist - period * floor(dist / period);
                let along = min(x, period - x);
                let rdot = max(dashLen * 0.5, 0.5);
                let d2 = sqrt(sd * sd + along * along);
                coverage = 1.0 - smoothstep(-1.0, 1.0, d2 - rdot);
            }
        }
        else
        {
            coverage = 1.0 - smoothstep(-1.0, 0.0, sd);
        }
    }
    else
    {
        // Fill.
        coverage = 1.0 - smoothstep(-1.0, 0.0, sd);
    }

    var color = inst.color;
    if (inst.flags.y > 0.0)
    {
        color = evalGradient(inst, input.local);
    }

    let alpha = clamp(color.a * coverage * clipMask(inst, frag.xy), 0.0, 1.0);
    return float4(Color::SrgbToLinear(color.rgb), alpha);
}
