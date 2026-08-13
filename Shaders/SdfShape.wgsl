// Batched SDF shape renderer (Crowbar 2D UI backend).
//
// One instanced unit quad is drawn per shape. The fragment shader evaluates
// the shape as a signed-distance field, paints it with a solid color or an
// inline gradient (up to four stops baked into the instance), and multiplies
// through up to four stacked screen-space clips. A single DrawInstanced call
// therefore covers every rect / rounded-rect / circle / ellipse / line in the
// batch, which is what keeps the draw-call count independent of UI complexity.

struct SdfInstance {
    rect: vec4f,       // local bounding box: xy = top-left, zw = size
    m0: vec4f,         // transform row: screen.x = m0.x*lx + m0.y*ly + m0.z
    m1: vec4f,         // transform row: screen.y = m1.x*lx + m1.y*ly + m1.z
    color: vec4f,      // straight sRGB RGBA (used when the gradient kind is 0)
    params: vec4f,     // x = corner radius, y = stroke width (0 = fill), z/w unused
    grad0: vec4f,      // linear: start (local); radial: center (local); line: start
    grad1: vec4f,      // linear: end (local); radial: x = radius, y = focal; line: end
    stop0: vec4f,      // gradient stop colors (straight sRGB)
    stop1: vec4f,
    stop2: vec4f,
    stop3: vec4f,
    offsets: vec4f,    // gradient stop offsets (0..1, ascending)
    clip0: vec4f,      // screen-space clip rects (xy = top-left, zw = size)
    clip1: vec4f,
    clip2: vec4f,
    clip3: vec4f,
    clipRadii: vec4f,  // clip corner radii (px)
    flags: vec4f,      // x = shape kind, y = gradient kind, z = clip count, w = stop count
}

@group(0) @binding(0) var<storage, read> instances: array<SdfInstance>;
@group(0) @binding(1) var<uniform> viewport: vec4f; // xy = size, zw = 1/size

struct VertexOutput {
    @builtin(position) position: vec4f,
    @location(0) local: vec2f,
    // This wgpu build does not expose instance_index in the fragment stage, so
    // it is forwarded from the vertex shader as a flat varying.
    @location(1) @interpolate(flat) instance: u32,
}

@vertex
fn vs_main(@location(0) position: vec2f, @location(1) uv: vec2f,
           @builtin(instance_index) instanceIndex: u32) -> VertexOutput {
    var out: VertexOutput;
    let inst = instances[instanceIndex];
    let local = inst.rect.xy + uv * inst.rect.zw;
    let screen = vec2f(
        inst.m0.x * local.x + inst.m0.y * local.y + inst.m0.z,
        inst.m1.x * local.x + inst.m1.y * local.y + inst.m1.z);
    out.position = vec4f(screen.x * viewport.z * 2.0 - 1.0, 1.0 - screen.y * viewport.w * 2.0, 0.0, 1.0);
    out.local = local;
    out.instance = instanceIndex;
    return out;
}

fn srgbToLinear(c: vec3f) -> vec3f {
    let lo = c / 12.92;
    let hi = pow((c + 0.055) / 1.055, vec3f(2.4));
    return select(hi, lo, c <= vec3f(0.04045));
}

// Signed distance to a rounded rectangle (iquilezles.org/articles/distfunctions2d).
fn roundedRectSDF(p: vec2f, b: vec4f, r: f32) -> f32 {
    let half = b.zw * 0.5;
    let center = b.xy + half;
    let rr = min(r, min(half.x, half.y));
    let q = abs(p - center) - (half - vec2f(rr));
    return length(max(q, vec2f(0.0))) + min(max(q.x, q.y), 0.0) - rr;
}

// Signed distance to an axis-aligned ellipse inscribed in the rect.
fn ellipseSDF(p: vec2f, b: vec4f) -> f32 {
    let half = b.zw * 0.5;
    let center = b.xy + half;
    let q = (p - center) / half;
    return (length(q) - 1.0) * min(half.x, half.y);
}

// Distance to a line segment (the clamped projection yields round caps).
fn segmentSDF(p: vec2f, a: vec2f, b: vec2f) -> f32 {
    let pa = p - a;
    let ba = b - a;
    let h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
    return length(pa - ba * h);
}

// The gradient stops are indexed with constant indices only (naga rejects
// dynamic indices into struct-member arrays); the count gates each step.
fn stopColor(inst: SdfInstance, i: u32) -> vec4f {
    var c = inst.stop0;
    if (i == 1u) { c = inst.stop1; }
    else if (i == 2u) { c = inst.stop2; }
    else if (i == 3u) { c = inst.stop3; }
    return c;
}

fn stopOffset(inst: SdfInstance, i: u32) -> f32 {
    var o = inst.offsets.x;
    if (i == 1u) { o = inst.offsets.y; }
    else if (i == 2u) { o = inst.offsets.z; }
    else if (i == 3u) { o = inst.offsets.w; }
    return o;
}

fn evalGradient(inst: SdfInstance, local: vec2f) -> vec4f {
    let kind = inst.flags.y;
    var t = 0.0;
    if (kind == 1.0) {
        let d = inst.grad1.xy - inst.grad0.xy;
        let denom = dot(d, d);
        t = denom > 0.0 ? clamp(dot(local - inst.grad0.xy, d) / denom, 0.0, 1.0) : 0.0;
    } else if (kind == 2.0) {
        let r = max(inst.grad1.x, 0.0001);
        t = clamp(length(local - inst.grad0.xy) / r, 0.0, 1.0);
    }
    let count = clamp(u32(inst.flags.w), 1u, 4u);
    var color = stopColor(inst, 0u);
    var prevOff = stopOffset(inst, 0u);
    var prevCol = color;
    for (var i = 1u; i < 4u; i++) {
        if (i < count) {
            let off = stopOffset(inst, i);
            let col = stopColor(inst, i);
            if (t >= prevOff && t <= off) {
                let span = max(off - prevOff, 0.0001);
                color = mix(prevCol, col, clamp((t - prevOff) / span, 0.0, 1.0));
            }
            prevOff = off;
            prevCol = col;
        }
    }
    return color;
}

fn clipRect(inst: SdfInstance, i: u32) -> vec4f {
    var c = inst.clip0;
    if (i == 1u) { c = inst.clip1; }
    else if (i == 2u) { c = inst.clip2; }
    else if (i == 3u) { c = inst.clip3; }
    return c;
}

fn clipRadius(inst: SdfInstance, i: u32) -> f32 {
    var r = inst.clipRadii.x;
    if (i == 1u) { r = inst.clipRadii.y; }
    else if (i == 2u) { r = inst.clipRadii.z; }
    else if (i == 3u) { r = inst.clipRadii.w; }
    return r;
}

// Product of the (up to four) screen-space clip masks, each a rounded rect.
fn clipMask(inst: SdfInstance, frag: vec2f) -> f32 {
    var mask = 1.0;
    let count = u32(inst.flags.z);
    if (count > 0u) { mask *= 1.0 - smoothstep(-1.0, 0.0, roundedRectSDF(frag, clipRect(inst, 0u), clipRadius(inst, 0u))); }
    if (count > 1u) { mask *= 1.0 - smoothstep(-1.0, 0.0, roundedRectSDF(frag, clipRect(inst, 1u), clipRadius(inst, 1u))); }
    if (count > 2u) { mask *= 1.0 - smoothstep(-1.0, 0.0, roundedRectSDF(frag, clipRect(inst, 2u), clipRadius(inst, 2u))); }
    if (count > 3u) { mask *= 1.0 - smoothstep(-1.0, 0.0, roundedRectSDF(frag, clipRect(inst, 3u), clipRadius(inst, 3u))); }
    return mask;
}

@fragment
fn fs_main(@builtin(position) frag: vec4f, @location(0) local: vec2f,
           @location(1) @interpolate(flat) instance: u32) -> @location(0) vec4f {
    let inst = instances[instance];
    let kind = inst.flags.x;

    var sd = 0.0;
    if (kind == 3.0) {
        // Line segment (round caps fall out of the clamped projection).
        sd = segmentSDF(local, inst.grad0.xy, inst.grad1.xy) - max(inst.params.y * 0.5, 0.0);
    } else if (kind == 2.0) {
        sd = ellipseSDF(local, inst.rect);
    } else {
        sd = roundedRectSDF(local, inst.rect, inst.params.x);
    }

    // A stroke is the ring between |sd| <= width/2; a fill is the interior.
    var coverage = 0.0;
    let strokeWidth = inst.params.y;
    if (strokeWidth > 0.0 && kind != 3.0) {
        coverage = 1.0 - smoothstep(-1.0, 0.0, abs(sd) - strokeWidth * 0.5);
    } else {
        coverage = 1.0 - smoothstep(-1.0, 0.0, sd);
    }

    var color = inst.color;
    if (inst.flags.y > 0.0) {
        color = evalGradient(inst, local);
    }

    let alpha = clamp(color.a * coverage * clipMask(inst, frag.xy), 0.0, 1.0);
    return vec4f(srgbToLinear(color.rgb), alpha);
}
