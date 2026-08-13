// SDF glyph renderer (Crowbar 2D UI backend).
//
// Text is rasterized once into a single-channel signed distance field packed
// in the glyph atlas. Each glyph is a screen-space quad carrying the atlas UV
// and a straight sRGB color; the fragment shader reconstructs the distance and
// smoothsteps it for antialiasing, so text stays crisp at any scale with no
// per-size rasterization.

@group(0) @binding(0) var glyph_atlas: texture_2d<f32>;
@group(0) @binding(1) var glyph_sampler: sampler;
@group(0) @binding(2) var<uniform> viewport: vec4f; // xy = size, zw = 1/size

// Must match GlyphRasterizer.Spread and the antialiasing band width.
const SPREAD: f32 = 8.0;
const AA: f32 = 0.75;

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

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4f {
    let s = textureSample(glyph_atlas, glyph_sampler, input.uv).r;
    let distance = (s - 0.5) * SPREAD;
    let coverage = smoothstep(-AA, AA, distance);
    return vec4f(srgbToLinear(input.color.rgb), coverage * input.color.a);
}
