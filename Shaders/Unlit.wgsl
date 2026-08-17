#include "Common/Transform.wgsl"
#include "Common/VertexIO.wgsl"

struct MaterialUniforms {
    color: vec4<f32>,
    metallic: f32,
    roughness: f32,
    emissive: f32,
};

@group(1) @binding(0) var<uniform> model: mat4x4<f32>;
@group(1) @binding(1) var<uniform> material: MaterialUniforms;

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    let world_position = model * vec4<f32>(input.position, 1.0);
    output.clip_position = scene.proj * scene.view * world_position;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return material.color;
}
