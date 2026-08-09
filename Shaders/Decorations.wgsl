// GPU decoration compositor for outer box-shadows and uniform solid borders.
// One instanced quad per region is drawn above the UI texture (after the UI
// overlay in the surface pass). The Skia raster skips these decorations when
// they are delegated (see SkiaUiRenderer.CollectDecorations), so the CPU never
// builds their paths or blur masks — the cost is a few SDF evaluations per
// covered pixel.
//
// Ordering contract: a decoration is only delegated when nothing painted after
// its panel can cover it and no ancestor clip cuts it, so compositing above
// the flat UI texture reproduces the CSS paint order. The shadow quad clips
// the panel's own border box out of itself (that area already holds the
// panel's background and content in the texture).

struct DecorationParams {
    quad: vec4f,   // xy = rasterization-bounds top-left (px), zw = size (px)
    box: vec4f,    // xy = panel border-box top-left (px), zw = size (px)
    shape: vec4f,  // xy = shadow shape top-left (px), zw = size (px) (borders: == box)
    radii: vec4f,  // x = shape corner radius (px), y = shadow blur (px), z = shadow spread (px), w = border width (px)
    color: vec4f,  // straight sRGB RGBA (0..1); a = effective alpha (opacity baked in)
    flags: vec4f,  // x = kind: 0 = outer shadow, 1 = border
}

@group(0) @binding(0) var<storage, read> params: array<DecorationParams>;
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
    let screen = p.quad.xy + uv * p.quad.zw;
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
    let pos = frag.xy;
    var alpha = 0.0;
    if (p.flags.x < 0.5) {
        // Outer box-shadow: the blurred rounded-rect shape, with the panel's
        // own border box clipped out (its interior is already painted in the
        // UI texture). The blur is a linear falloff across the blur radius.
        let sdShape = roundedRectSDF(pos, p.shape, p.radii.x);
        let blur = max(p.radii.y, 1.0);
        let shapeAlpha = clamp(0.5 - sdShape / blur, 0.0, 1.0);
        let boxRadius = max(0.0, p.radii.x - p.radii.z);
        let sdBox = roundedRectSDF(pos, p.box, boxRadius);
        let boxMask = 1.0 - smoothstep(-1.0, 0.0, sdBox);
        alpha = shapeAlpha * (1.0 - boxMask);
    } else {
        // Uniform solid border: the ring between the border box and the box
        // inset by the border width, both following the rounded corners.
        let w = p.radii.w;
        let sdOuter = roundedRectSDF(pos, p.box, p.radii.x);
        let innerBox = vec4f(p.box.x + w, p.box.y + w, max(p.box.z - 2.0 * w, 0.0), max(p.box.w - 2.0 * w, 0.0));
        let innerRadius = max(0.0, p.radii.x - w);
        let sdInner = roundedRectSDF(pos, innerBox, innerRadius);
        let outer = 1.0 - smoothstep(-1.0, 0.0, sdOuter);
        let inner = 1.0 - smoothstep(-1.0, 0.0, sdInner);
        alpha = outer * (1.0 - inner);
    }
    return vec4f(srgbToLinear(p.color.rgb), p.color.a * clamp(alpha, 0.0, 1.0));
}
