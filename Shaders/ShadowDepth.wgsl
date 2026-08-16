// Depth-only shadow pass. Renders a mesh's position through the current
// light-face view-projection matrix into the shadow atlas. There is no color
// output: the rasterizer writes the interpolated clip depth automatically, so
// the fragment stage is an empty entry point.

@group(0) @binding(0) var<uniform> lightViewProj: mat4x4<f32>;
@group(1) @binding(0) var<uniform> model: mat4x4<f32>;

struct VertexInput {
    @location(0) position: vec3<f32>,
}

@vertex
fn vs_main(input: VertexInput) -> @builtin(position) vec4<f32> {
    let worldPosition = model * vec4<f32>(input.position, 1.0);
    return lightViewProj * worldPosition;
}

@fragment
fn fs_main() {
}
