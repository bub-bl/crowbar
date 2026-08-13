// GPU CSS-filter compositor (Crowbar 2D UI backend).
//
// A filtered subtree is rendered to an offscreen target; this pass composites
// it back onto its parent by sampling the target with a radial multi-tap blur
// and applying the color filter operations in declaration order. The ops
// mirror Backdrop.wgsl's applyOp with grayscale added; the operation types
// match FilterOpKind in Filter2D.cs (0 blur, 1 brightness, 2 contrast,
// 3 grayscale, 4 hue-rotate, 5 invert, 6 opacity, 7 saturate, 8 sepia).

struct FilterParams {
    blur: vec4f,          // x = blur radius (px)
    ops: array<vec4f, 8>, // x = op type, y = amount
    opCount: vec4f,       // x = number of ops
}

@group(0) @binding(0) var source: texture_2d<f32>;
@group(0) @binding(1) var source_sampler: sampler;
@group(0) @binding(2) var<storage, read> params: FilterParams;

struct VertexOutput {
    @builtin(position) position: vec4f,
    @location(0) uv: vec2f,
}

@vertex
fn vs_main(@location(0) position: vec2f, @location(1) uv: vec2f) -> VertexOutput {
    var out: VertexOutput;
    out.position = vec4f(position.x * 2.0 - 1.0, 1.0 - position.y * 2.0, 0.0, 1.0);
    out.uv = uv;
    return out;
}

fn applyOp(c: vec4f, op: vec4f) -> vec4f {
    var color = c;
    let opType = u32(op.x);
    let amount = op.y;
    if (opType == 1u) {
        // brightness
        color = vec4f(color.rgb * amount, color.a);
    } else if (opType == 2u) {
        // contrast
        color = vec4f(clamp((color.rgb - vec3f(0.5)) * amount + vec3f(0.5), vec3f(0.0), vec3f(1.0)), color.a);
    } else if (opType == 3u) {
        // grayscale
        let luma = dot(color.rgb, vec3f(0.2126, 0.7152, 0.0722));
        color = vec4f(mix(color.rgb, vec3f(luma), clamp(amount, 0.0, 1.0)), color.a);
    } else if (opType == 4u) {
        // hue-rotate (degrees): the canonical CSS Filter Effects matrix.
        let rad = amount * 3.14159265358979 / 180.0;
        let cosA = cos(rad);
        let sinA = sin(rad);
        let r = color.r;
        let g = color.g;
        let b = color.b;
        color.r = (0.213 + cosA * 0.787 - sinA * 0.213) * r
            + (0.715 - cosA * 0.715 - sinA * 0.715) * g
            + (0.072 - cosA * 0.072 + sinA * 0.928) * b;
        color.g = (0.213 - cosA * 0.213 + sinA * 0.143) * r
            + (0.715 + cosA * 0.285 + sinA * 0.140) * g
            + (0.072 - cosA * 0.072 - sinA * 0.283) * b;
        color.b = (0.213 - cosA * 0.213 - sinA * 0.787) * r
            + (0.715 - cosA * 0.715 + sinA * 0.715) * g
            + (0.072 + cosA * 0.928 + sinA * 0.072) * b;
    } else if (opType == 5u) {
        // invert
        color = vec4f(mix(color.rgb, vec3f(1.0) - color.rgb, clamp(amount, 0.0, 1.0)), color.a);
    } else if (opType == 6u) {
        // opacity
        color.a *= clamp(amount, 0.0, 1.0);
    } else if (opType == 7u) {
        // saturate
        let luma = dot(color.rgb, vec3f(0.2126, 0.7152, 0.0722));
        color = vec4f(mix(vec3f(luma), color.rgb, amount), color.a);
    } else if (opType == 8u) {
        // sepia (amount 0..1, 1 = full sepia)
        let s = clamp(amount, 0.0, 1.0);
        let m = 1.0 - s;
        let r = color.r;
        let g = color.g;
        let b = color.b;
        color.r = (0.393 + 0.607 * m) * r + (0.769 - 0.769 * m) * g + (0.189 - 0.189 * m) * b;
        color.g = (0.349 - 0.349 * m) * r + (0.686 + 0.314 * m) * g + (0.168 - 0.168 * m) * b;
        color.b = (0.272 - 0.272 * m) * r + (0.534 - 0.534 * m) * g + (0.131 + 0.869 * m) * b;
    }
    return color;
}

fn applyOps(c: vec4f, p: FilterParams) -> vec4f {
    var color = c;
    let count = p.opCount.x;
    if (count > 0.0) { color = applyOp(color, p.ops[0]); }
    if (count > 1.0) { color = applyOp(color, p.ops[1]); }
    if (count > 2.0) { color = applyOp(color, p.ops[2]); }
    if (count > 3.0) { color = applyOp(color, p.ops[3]); }
    if (count > 4.0) { color = applyOp(color, p.ops[4]); }
    if (count > 5.0) { color = applyOp(color, p.ops[5]); }
    if (count > 6.0) { color = applyOp(color, p.ops[6]); }
    if (count > 7.0) { color = applyOp(color, p.ops[7]); }
    return color;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4f {
    var color = textureSample(source, source_sampler, input.uv);

    // Radial multi-tap blur of the source (16 directions x 3 steps), matching
    // Backdrop.wgsl. Blur is applied before the color ops.
    let blurSigma = params.blur.x;
    if (blurSigma > 0.0) {
        let size = vec2f(blurSigma) / vec2f(textureDimensions(source));
        for (var d = 0u; d < 16u; d++) {
            let angle = f32(d) * 6.28318530718 / 16.0;
            let dir = vec2f(cos(angle), sin(angle));
            for (var j = 1u; j <= 3u; j++) {
                let t = f32(j) / 3.0;
                color += textureSample(source, source_sampler, input.uv + dir * size * t);
            }
        }
        color /= 49.0;
    }

    return applyOps(color, params);
}
