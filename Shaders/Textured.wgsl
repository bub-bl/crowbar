// Textured-quad renderer (Crowbar 2D UI backend).
//
// Images are packed into a single atlas texture and drawn as screen-space
// quads carrying a UV rectangle and a straight sRGB tint. One Draw call
// renders every image in the batch; the tint lets the same atlas entry be
// reused at different opacities/colors (icons, buttons, editor thumbnails).

@group(0) @binding(0) var image_atlas: texture_2d<f32>;
@group(0) @binding(1) var image_sampler: sampler;
@group(0) @binding(2) var<uniform> viewport: vec4f; // xy = size, zw = 1/size

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
    let tex = textureSample(image_atlas, image_sampler, input.uv);
    let rgb = srgbToLinear(tex.rgb) * srgbToLinear(input.color.rgb);
    return vec4f(rgb, tex.a * input.color.a);
}
