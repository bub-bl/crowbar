// MSDF glyph shadow renderer (Crowbar 2D UI backend).
//
// A text shadow reuses the glyph atlas: the same glyph quad is drawn once at an
// offset with the shadow color, but with a wider smoothstep band (softness), so
// the multi-channel distance field falls off across the blur radius instead of
// the fixed 1px antialiasing band. The distance is the median of the three MSDF
// channels, matching Glyph.wgsl. One Draw renders every glyph shadow in the
// batch.

@group(0) @binding(0) var glyph_atlas: texture_2d<f32>;
@group(0) @binding(1) var glyph_sampler: sampler;
@group(0) @binding(2) var<uniform> viewport: vec4f; // xy = size, zw = 1/size

// Must match GlyphRasterizer.Spread * Scale (the atlas is rasterized at 2×,
// so distances come back in grid units).
const SPREAD: f32 = 16.0;

struct VertexOutput {
    @builtin(position) position: vec4f,
    @location(0) uv: vec2f,
    @location(1) color: vec4f,
    @location(2) @interpolate(flat) softness: f32,
}

@vertex
fn vs_main(@location(0) position: vec2f, @location(1) uv: vec2f,
           @location(2) color: vec4f, @location(3) softness: f32) -> VertexOutput {
    var out: VertexOutput;
    out.position = vec4f(position.x * viewport.z * 2.0 - 1.0, 1.0 - position.y * viewport.w * 2.0, 0.0, 1.0);
    out.uv = uv;
    out.color = color;
    out.softness = softness;
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

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4f {
    let s = textureSample(glyph_atlas, glyph_sampler, input.uv).rgb;
    let distance = (median(s.r, s.g, s.b) - 0.5) * SPREAD;
    let soft = max(input.softness, 0.75);
    let coverage = smoothstep(-soft, soft, distance);
    return vec4f(srgbToLinear(input.color.rgb), coverage * input.color.a);
}
