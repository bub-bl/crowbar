// Tessellated-triangle renderer (Crowbar 2D UI backend).
//
// Polygons (and, later, SVG paths, glyph quads and image quads) are
// tessellated on the CPU into screen-space triangles carrying a straight sRGB
// color. This shader projects the already-transformed positions and decodes
// the color; it is the fallback path for geometry the SDF shader cannot
// express, batched into a single Draw call per frame.

@group(0) @binding(0) var<uniform> viewport: vec4f; // xy = size, zw = 1/size

struct VertexOutput {
    @builtin(position) position: vec4f,
    @location(0) color: vec4f,
}

@vertex
fn vs_main(@location(0) position: vec2f, @location(1) color: vec4f) -> VertexOutput {
    var out: VertexOutput;
    out.position = vec4f(position.x * viewport.z * 2.0 - 1.0, 1.0 - position.y * viewport.w * 2.0, 0.0, 1.0);
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
    return vec4f(srgbToLinear(input.color.rgb), input.color.a);
}
