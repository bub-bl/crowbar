// MSDF glyph renderer (Crowbar 2D UI backend).
//
// Text is rasterized once into a multi-channel signed distance field (MSDF)
// packed in the glyph atlas: RGB hold the three channel distances and alpha
// retains the true signed distance for CPU-side correction/debugging. The
// fragment shader reconstructs the MSDF distance as the median of the three
// channels (Chlumský's msdfgen), which stays exact at sharp corners under
// bilinear filtering — a single-channel SDF would round them. Each glyph is a
// screen-space quad carrying the atlas UV and a straight sRGB color, so text
// stays crisp at any scale with no per-size rasterization.

@group(0) @binding(0) var glyph_atlas: texture_2d<f32>;
@group(0) @binding(1) var glyph_sampler: sampler;
@group(0) @binding(2) var<uniform> viewport: vec4f; // xy = size, zw = 1/size

// Must match GlyphRasterizer.Spread * GlyphRasterizer.Scale. This is the
// representable distance range in atlas texels.
const SPREAD: f32 = 16.0;

struct VertexOutput {
    @builtin(position) position: vec4f,
    @location(0) uv: vec2f,
    @location(1) color: vec4f,
}

@vertex
fn vs_main(@location(0) position: vec2f, @location(1) uv: vec2f,
           @location(2) color: vec4f) -> VertexOutput {
    var out: VertexOutput;
    out.position = vec4f(position.x * viewport.z * 2.0 - 1.0, 1.0 - position.y * viewport.w * 2.0, 0.0, 1.0);
    out.uv = uv;
    out.color = color;
    return out;
}

fn srgbToLinear(c: vec3f) -> vec3f {
    let lo = c / 12.92;
    let hi = pow((c + 0.055) / 1.055, vec3f(2.4));
    return select(hi, lo, c <= vec3f(0.04045));
}

fn median(r: f32, g: f32, b: f32) -> f32 {
    return max(min(r, g), min(max(r, g), b));
}

// Reference MSDF antialiasing from msdfgen. Derive the screen-space range
// from the UV gradient, rather than from the reconstructed distance: the
// median has discontinuities at edge-color transitions, so fwidth(distance)
// can turn a valid contour into a broad horizontal smear.
fn screenPxRange(uv: vec2f) -> f32 {
    let unitRange = vec2f(SPREAD) / vec2f(textureDimensions(glyph_atlas));
    let dx = dpdx(uv);
    let dy = dpdy(uv);
    let screenTexSize = inverseSqrt(dx * dx + dy * dy);
    return max(0.5 * dot(unitRange, screenTexSize), 1.0);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4f {
    let msd = textureSample(glyph_atlas, glyph_sampler, input.uv).rgb;
    let coverage = clamp(screenPxRange(input.uv) * (median(msd.r, msd.g, msd.b) - 0.5) + 0.5, 0.0, 1.0);
    return vec4f(srgbToLinear(input.color.rgb), coverage * input.color.a);
}
