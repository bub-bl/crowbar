// GPU background compositor for solid panel fills. One instanced quad per
// region is drawn below the UI texture (right after the scene blit in the
// surface pass). The Skia raster skips these fills when they are delegated
// (see SkiaUiRenderer.CollectFills), so the CPU never builds their rounded
// paths or runs the fill kernels — the cost is a few SDF evaluations per
// covered pixel.
//
// Ordering contract: a fill is only delegated when nothing painted before its
// panel has content in the fill area (the accumulator in CollectFills) and no
// ancestor clip cuts it, so the texture is transparent where the quad is and
// the quads stack in paint order under the flat UI layer. The quad replaces
// the panel's own background paint: the panel's content (text, children) is
// still in the texture above it.

struct FillParams {
    rect: vec4f,   // xy = border-box top-left (px), zw = size (px)
    color: vec4f,  // straight sRGB RGBA (0..1); a = effective alpha (opacity baked in)
    flags: vec4f,  // x = corner radius (px)
}

@group(0) @binding(0) var<storage, read> params: array<FillParams>;
@group(0) @binding(1) var<uniform> viewport: vec4f;

struct VertexOutput {
    @builtin(position) position: vec4f,
    @location(1) @interpolate(flat) instance: u32,
}

@vertex
fn vs_main(@location(0) position: vec2f, @location(1) uv: vec2f,
           @builtin(instance_index) instanceIndex: u32) -> VertexOutput {
    var out: VertexOutput;
    let p = params[instanceIndex];
    // Map the unit quad onto the region's pixel rect, then into clip space.
    let screen = p.rect.xy + uv * p.rect.zw;
    let ndc = vec2f(screen.x / viewport.x * 2.0 - 1.0, 1.0 - screen.y / viewport.y * 2.0);
    out.position = vec4f(ndc, 0.0, 1.0);
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

@fragment
fn fs_main(@builtin(position) frag: vec4f, @location(1) @interpolate(flat) instance: u32) -> @location(0) vec4f {
    let p = params[instance];
    let sd = roundedRectSDF(frag.xy, p.rect, p.flags.x);
    // Antialiased rounded-rect fill, matching Skia's DrawRoundRect edge.
    let alpha = 1.0 - smoothstep(-1.0, 0.0, sd);
    return vec4f(srgbToLinear(p.color.rgb), p.color.a * clamp(alpha, 0.0, 1.0));
}
