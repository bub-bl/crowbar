// GPU backdrop-filter compositor, mirroring S&box's ui_backdropfilter.shader.
// One instanced fullscreen quad is drawn per backdrop-filter region between the
// 3D scene blit and the UI overlay: the fragment samples the scene texture
// (the WebGPU 3D viewport), blurs it with a radial multi-tap kernel, applies
// the CSS color transforms in declaration order, overlays the panel's own
// background color, and writes the result inside the rounded border box.
//
// This is what keeps the frame rate up: the CPU never sees the 3D scene and
// the Skia UI raster only re-runs when the UI itself changes, so moving the
// camera costs nothing on the UI side.

struct BackdropParams {
    region: vec4f,     // xy = border-box top-left in screen px, zw = size px
    uvRect: vec4f,     // normalized scene-texture coords of the region (u0, v0, u1, v1)
    radiusBlur: vec4f, // x = corner radius px, y = blur sigma px, z = panel alpha, w = unused
    tint: vec4f,       // the panel's background color (straight alpha), painted over the backdrop
    ops: array<vec4f, 8>, // x = op type, y = amount; op types: 0 brightness, 1 contrast,
                          // 2 saturate, 3 invert, 4 hue-rotate (deg), 5 sepia, 6 opacity
    opCount: vec4f,    // x = number of active ops
}

@group(0) @binding(0) var scene: texture_2d<f32>;
@group(0) @binding(1) var scene_sampler: sampler;
@group(0) @binding(2) var<storage, read> params: array<BackdropParams>;

struct VertexOutput {
    @builtin(position) position: vec4f,
    // The instance index selects the region's params; this wgpu build does not
    // expose instance_index in the fragment stage, so it is forwarded from the
    // vertex shader as a flat varying.
    @location(1) @interpolate(flat) instance: u32,
}

@vertex
fn vs_main(@location(0) position: vec2f, @location(1) uv: vec2f,
           @builtin(instance_index) instanceIndex: u32) -> VertexOutput {
    var out: VertexOutput;
    out.position = vec4f(position, 0.0, 1.0);
    out.instance = instanceIndex;
    return out;
}

fn applyOp(color: vec4f, op: vec4f) -> vec4f {
    var c = color;
    let opType = op.x;
    let amount = op.y;
    if (opType == 0.0) {
        // brightness
        c = vec4f(c.rgb * amount, c.a);
    } else if (opType == 1.0) {
        // contrast
        c = vec4f(clamp((c.rgb - vec3f(0.5)) * amount + vec3f(0.5), vec3f(0.0), vec3f(1.0)), c.a);
    } else if (opType == 2.0) {
        // saturate
        let luma = dot(c.rgb, vec3f(0.2126, 0.7152, 0.0722));
        c = vec4f(mix(vec3f(luma), c.rgb, amount), c.a);
    } else if (opType == 3.0) {
        // invert
        c = vec4f(mix(c.rgb, vec3f(1.0) - c.rgb, amount), c.a);
    } else if (opType == 4.0) {
        // hue-rotate (degrees): the canonical CSS Filter Effects matrix.
        let rad = amount * 3.14159265358979 / 180.0;
        let cosA = cos(rad);
        let sinA = sin(rad);
        let r = c.r;
        let g = c.g;
        let b = c.b;
        c.r = (0.213 + cosA * 0.787 - sinA * 0.213) * r
            + (0.715 - cosA * 0.715 - sinA * 0.715) * g
            + (0.072 - cosA * 0.072 + sinA * 0.928) * b;
        c.g = (0.213 - cosA * 0.213 + sinA * 0.143) * r
            + (0.715 + cosA * 0.285 + sinA * 0.140) * g
            + (0.072 - cosA * 0.072 - sinA * 0.283) * b;
        c.b = (0.213 - cosA * 0.213 - sinA * 0.787) * r
            + (0.715 - cosA * 0.715 + sinA * 0.715) * g
            + (0.072 + cosA * 0.928 + sinA * 0.072) * b;
    } else if (opType == 5.0) {
        // sepia (amount 0..1, 1 = full sepia)
        let m = 1.0 - amount;
        let r = c.r;
        let g = c.g;
        let b = c.b;
        c.r = (0.393 + 0.607 * m) * r + (0.769 - 0.769 * m) * g + (0.189 - 0.189 * m) * b;
        c.g = (0.349 - 0.349 * m) * r + (0.686 + 0.314 * m) * g + (0.168 - 0.168 * m) * b;
        c.b = (0.272 - 0.272 * m) * r + (0.534 - 0.534 * m) * g + (0.131 + 0.869 * m) * b;
    } else if (opType == 6.0) {
        // opacity
        c.a *= amount;
    }
    return c;
}

// The ops array is indexed with constant indices (naga rejects dynamic
// indices into struct-member arrays); the runtime count gates each step.
fn applyOps(color: vec4f, p: BackdropParams) -> vec4f {
    var c = color;
    let count = u32(p.opCount.x);
    if (count > 0u) { c = applyOp(c, p.ops[0]); }
    if (count > 1u) { c = applyOp(c, p.ops[1]); }
    if (count > 2u) { c = applyOp(c, p.ops[2]); }
    if (count > 3u) { c = applyOp(c, p.ops[3]); }
    if (count > 4u) { c = applyOp(c, p.ops[4]); }
    if (count > 5u) { c = applyOp(c, p.ops[5]); }
    if (count > 6u) { c = applyOp(c, p.ops[6]); }
    if (count > 7u) { c = applyOp(c, p.ops[7]); }
    return c;
}

@fragment
fn fs_main(@builtin(position) frag: vec4f, @location(1) @interpolate(flat) instance: u32) -> @location(0) vec4f {
    let p = params[instance];

    // Normalized position inside the border box, then the matching scene uv.
    let rel = (frag.xy - p.region.xy) / p.region.zw;
    let uv = p.uvRect.xy + rel * p.uvRect.zw;

    // Rounded-rectangle signed distance (iquilezles.org/articles/distfunctions2d)
    // for the border-box mask with one pixel of antialiasing.
    let half = p.region.zw * 0.5;
    let center = p.region.xy + half;
    let radius = min(p.radiusBlur.x, min(half.x, half.y));
    let q = abs(frag.xy - center) - (half - vec2f(radius));
    let sd = length(max(q, vec2f(0.0))) + min(max(q.x, q.y), 0.0) - radius;
    let mask = 1.0 - smoothstep(-1.0, 0.0, sd);

    // Radial multi-tap blur of the scene (16 directions x 3 steps), applied
    // directly on the source texture like S&box's ui_backdropfilter.
    var color = textureSampleLevel(scene, scene_sampler, uv, 0.0);
    let blurSigma = p.radiusBlur.y;
    if (blurSigma > 0.0) {
        let size = vec2f(blurSigma) / vec2f(textureDimensions(scene));
        for (var d = 0u; d < 16u; d++) {
            let angle = f32(d) * 6.28318530718 / 16.0;
            let dir = vec2f(cos(angle), sin(angle));
            for (var j = 1u; j <= 3u; j++) {
                let t = f32(j) / 3.0;
                color += textureSampleLevel(scene, scene_sampler, uv + dir * size * t, 0.0);
            }
        }
        color /= 49.0;
    }

    color = applyOps(color, p);

    // The panel's own background color paints over the filtered backdrop.
    color = vec4f(mix(color.rgb, p.tint.rgb, p.tint.a), color.a);

    return vec4f(color.rgb, mask * color.a * p.radiusBlur.z);
}
