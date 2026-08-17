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
    out.position = float4(screen.x * viewport.z * 2.0 - 1.0, 1.0 - screen.y * viewport.w * 2.0, 0.0, 1.0);
    out.local = local;
    out.instance = instanceIndex;
    return out;
}

float3 srgbToLinear(float3 c)
{
    let lo = c / 12.92;
    let hi = pow((c + 0.055) / 1.055, float3(2.4));
    return select(c <= float3(0.04045), lo, hi);
}

// Signed distance to a rounded rectangle (iquilezles.org/articles/distfunctions2d).
float roundedRectSDF(float2 p, float4 b, float r)
{
    let half = b.zw * 0.5;
    let center = b.xy + half;
    let rr = min(r, min(half.x, half.y));
    let q = abs(p - center) - (half - float2(rr));
    return length(max(q, float2(0.0))) + min(max(q.x, q.y), 0.0) - rr;
}

// Signed distance to an axis-aligned ellipse inscribed in the rect.
float ellipseSDF(float2 p, float4 b)
{
    let half = b.zw * 0.5;
    let center = b.xy + half;
    let q = (p - center) / half;
    return (length(q) - 1.0) * min(half.x, half.y);
}

// Distance to a line segment (the clamped projection yields round caps).
float segmentSDF(float2 p, float2 a, float2 b)
{
    let pa = p - a;
    let ba = b - a;
    let h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
    return length(pa - ba * h);
}

// Arc length (local px) along a (rounded) rectangle outline, clockwise from
// the top edge's left end. Straight edges use the nearest-edge distance;
// corners use the quarter-arc angle (the arcs collapse to zero length when
// the corner radius is 0). Used to phase dashed/dotted borders.
float rectOutlineDist(float2 local, float4 b, float r)
{
    let half = b.zw * 0.5;
    let center = b.xy + half;
    let rr = min(r, min(half.x, half.y));
    let ax = max(half.x - rr, 0.0);
    let ay = max(half.y - rr, 0.0);
    let arcLen = rr * 1.5707963267948966; // quarter arc = r * pi / 2
    let perim = 4.0 * (ax + ay + arcLen);
    if (perim <= 0.0001) { return 0.0; }

    let q = local - center;
    let topEnd = 2.0 * ax;
    let rightArcEnd = topEnd + arcLen;
    let rightEnd = rightArcEnd + 2.0 * ay;
    let bottomArcEnd = rightEnd + arcLen;
    let bottomEnd = bottomArcEnd + 2.0 * ax;
    let leftArcEnd = bottomEnd + arcLen;
    let leftEnd = leftArcEnd + 2.0 * ay;
    let halfPi = 1.5707963267948966;
    let pi = 3.141592653589793;

    let qx = abs(q.x);
    let qy = abs(q.y);
    var u = 0.0;
    if (qx > ax && qy > ay)
    {
        if (q.x >= 0.0 && q.y <= 0.0) { u = topEnd + (atan2(q.y + ay, q.x - ax) + halfPi) * rr; }
        else if (q.x >= 0.0 && q.y >= 0.0) { u = rightEnd + atan2(q.y - ay, q.x - ax) * rr; }
        else if (q.x <= 0.0 && q.y >= 0.0) { u = bottomEnd + (atan2(q.y - ay, q.x + ax) - halfPi) * rr; }
        else
        {
            // Top-left corner: the angle sweeps pi (left) -> -pi/2 (top),
            // wrapping through the 2pi boundary.
            var theta = atan2(q.y + ay, q.x + ax);
            if (theta < 0.0) { theta = theta + 6.283185307179586; }
            u = leftEnd + (theta - pi) * rr;
        }
    }
    else
    {
        let dTop = abs(q.y + half.y);
        let dBot = abs(half.y - q.y);
        let dLef = abs(q.x + half.x);
        let dRig = abs(half.x - q.x);
        // Select the closest edge without float equality (WGSL forbids == on
        // floats); the four distances are compared directly.
        if (dTop <= dRig && dTop <= dBot && dTop <= dLef) { u = q.x + ax; }
        else if (dRig <= dBot && dRig <= dLef) { u = rightArcEnd + (q.y + ay); }
        else if (dBot <= dLef) { u = bottomArcEnd + (ax - q.x); }
        else { u = leftArcEnd + (ay - q.y); }
    }
    return clamp(u, 0.0, perim);
}

// Approximate arc length along an ellipse outline via the parametric angle.
// Exact for circles; for eccentric ellipses the dash spacing varies slightly.
float ellipseOutlineDist(float2 local, float4 b)
{
    let half = b.zw * 0.5;
    let center = b.xy + half;
    let q = (local - center) / half;
    var t = atan2(q.y, q.x);
    if (t < 0.0) { t = t + 6.283185307179586; } // 0..2pi
    let a = max(half.x, 0.0001);
    let c = max(half.y, 0.0001);
    let h = (a - c) * (a - c) / ((a + c) * (a + c));
    let perim = 3.141592653589793 * (a + c) * (1.0 + 3.0 * h / (10.0 + sqrt(4.0 - 3.0 * h)));
    return t / 6.283185307179586 * perim;
}

// Arc length (local px) along a line segment, clamped to its endpoints.
float lineOutlineDist(float2 local, float2 a, float2 b)
{
    let ba = b - a;
    let t = clamp(dot(local - a, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
    return t * length(ba);
}

// 1D on/off dash mask (local px): on for `dash` px, off for the gap (equal to
// `dash` for dashed borders, doubled for dotted so the dots breathe).
float dashMask(float dist, uint style, float dashLen)
{
    let dash = max(dashLen, 1.0);
    let gap = select(style == 2u, dash * 2.0, dash);
    let period = dash + gap;
    let x = dist - period * floor(dist / period);
    let d = abs(x - dash * 0.5) - dash * 0.5;
    return 1.0 - smoothstep(-1.0, 1.0, d);
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
    if (count > 0u) { mask *= 1.0 - smoothstep(-1.0, 0.0, roundedRectSDF(frag, clipRect(inst, 0u), clipRadius(inst, 0u))); }
    if (count > 1u) { mask *= 1.0 - smoothstep(-1.0, 0.0, roundedRectSDF(frag, clipRect(inst, 1u), clipRadius(inst, 1u))); }
    if (count > 2u) { mask *= 1.0 - smoothstep(-1.0, 0.0, roundedRectSDF(frag, clipRect(inst, 2u), clipRadius(inst, 2u))); }
    if (count > 3u) { mask *= 1.0 - smoothstep(-1.0, 0.0, roundedRectSDF(frag, clipRect(inst, 3u), clipRadius(inst, 3u))); }
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
        sd = segmentSDF(input.local, inst.grad0.xy, inst.grad1.xy) - max(inst.params.y * 0.5, 0.0);
    }
    else if (kind == 2u)
    {
        sd = ellipseSDF(input.local, inst.rect);
    }
    else
    {
        sd = roundedRectSDF(input.local, inst.rect, inst.params.x);
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
                ellipseOutlineDist(input.local, inst.rect),
                rectOutlineDist(input.local, inst.rect, inst.params.x));
            if (style == 1u)
            {
                coverage = (1.0 - smoothstep(-1.0, 0.0, abs(sd) - strokeWidth * 0.5)) * dashMask(dist, style, dashLen);
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
            let dist = lineOutlineDist(input.local, inst.grad0.xy, inst.grad1.xy);
            if (style == 1u)
            {
                coverage = (1.0 - smoothstep(-1.0, 0.0, sd)) * dashMask(dist, style, dashLen);
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
    return float4(srgbToLinear(color.rgb), alpha);
}
