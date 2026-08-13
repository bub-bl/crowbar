// Instanced soft shadow renderer (Crowbar 2D UI backend).
//
// One instanced unit quad is drawn per shadow. The fragment evaluates a
// rounded-rect signed distance field and produces a soft falloff across the
// blur radius, mirroring the existing Decorations.wgsl formula. Outer shadows
// clip the panel's own border box out of themselves; inner shadows are masked
// to the box interior. Up to four screen-space clips are applied, so shadows
// respect the same clip stack as the SDF shapes.

struct ShadowInstance {
    m0: vec4f,       // transform row: screen.x = m0.x*lx + m0.y*ly + m0.z
    m1: vec4f,       // transform row: screen.y = m1.x*lx + m1.y*ly + m1.z
    quad: vec4f,     // rasterization bounds (local): xy = top-left, zw = size
    shape: vec4f,    // shadow shape (local): xy = top-left, zw = size
    box: vec4f,      // panel border box (local): xy = top-left, zw = size
    radii: vec4f,    // x = shape corner radius, y = box corner radius, z = blur, w unused
    color: vec4f,    // straight sRGB RGBA
    flags: vec4f,    // x = kind (0 outer, 1 inner), y = clip count
    clip0: vec4f,    // screen-space clip rects (xy = top-left, zw = size)
    clip1: vec4f,
    clip2: vec4f,
    clip3: vec4f,
    clipRadii: vec4f, // clip corner radii (px)
}

@group(0) @binding(0) var<storage, read> instances: array<ShadowInstance>;
@group(0) @binding(1) var<uniform> viewport: vec4f; // xy = size, zw = 1/size

struct VertexOutput {
    @builtin(position) position: vec4f,
    @location(0) local: vec2f,
    @location(1) @interpolate(flat) instance: u32,
}

@vertex
fn vs_main(@location(0) position: vec2f, @location(1) uv: vec2f,
           @builtin(instance_index) instanceIndex: u32) -> VertexOutput {
    var out: VertexOutput;
    let inst = instances[instanceIndex];
    let local = inst.quad.xy + uv * inst.quad.zw;
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

fn roundedRectSDF(p: vec2f, b: vec4f, r: f32) -> f32 {
    let half = b.zw * 0.5;
    let center = b.xy + half;
    let rr = min(r, min(half.x, half.y));
    let q = abs(p - center) - (half - vec2f(rr));
    return length(max(q, vec2f(0.0))) + min(max(q.x, q.y), 0.0) - rr;
}

fn clipRect(inst: ShadowInstance, i: u32) -> vec4f {
    var c = inst.clip0;
    if (i == 1u) { c = inst.clip1; }
    else if (i == 2u) { c = inst.clip2; }
    else if (i == 3u) { c = inst.clip3; }
    return c;
}

fn clipRadius(inst: ShadowInstance, i: u32) -> f32 {
    var r = inst.clipRadii.x;
    if (i == 1u) { r = inst.clipRadii.y; }
    else if (i == 2u) { r = inst.clipRadii.z; }
    else if (i == 3u) { r = inst.clipRadii.w; }
    return r;
}

fn clipMask(inst: ShadowInstance, frag: vec2f) -> f32 {
    var mask = 1.0;
    let count = u32(inst.flags.y);
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
    let sdShape = roundedRectSDF(local, inst.shape, inst.radii.x);
    let blur = max(inst.radii.z, 1.0);
    let sdBox = roundedRectSDF(local, inst.box, inst.radii.y);
    let boxMask = 1.0 - smoothstep(-1.0, 0.0, sdBox);

    var alpha = 0.0;
    if (inst.flags.x < 0.5) {
        // Outer shadow: soft falloff outside the shape, with the box cut out.
        alpha = clamp(0.5 - sdShape / blur, 0.0, 1.0) * (1.0 - boxMask);
    } else {
        // Inner shadow: soft falloff inside the eroded shape, masked to the box.
        alpha = clamp(0.5 + sdShape / blur, 0.0, 1.0) * boxMask;
    }

    alpha *= clipMask(inst, frag.xy);
    return vec4f(srgbToLinear(inst.color.rgb), inst.color.a * clamp(alpha, 0.0, 1.0));
}
