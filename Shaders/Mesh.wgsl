#include "Common/Transform.wgsl"
#include "Common/Shadows.wgsl"
#include "Common/Lighting.wgsl"

// Material parameters, packed by the engine from this struct's fields.
struct MaterialUniforms {
    color: vec4<f32>,
    metallic: f32,
    roughness: f32,
    emissive: f32,
};

@group(1) @binding(0) var<uniform> model: mat4x4<f32>;
@group(1) @binding(1) var<uniform> material: MaterialUniforms;

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) tangent: vec4<f32>,
    @location(3) uv: vec2<f32>,
};

struct VertexOutput {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) world_position: vec3<f32>,
    @location(1) normal: vec3<f32>,
};

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    let world_position = model * vec4<f32>(input.position, 1.0);
    output.world_position = world_position.xyz;

    let normal_matrix = mat3x3<f32>(model[0].xyz, model[1].xyz, model[2].xyz);
    output.normal = normalize(normal_matrix * input.normal);
    output.clip_position = scene.proj * scene.view * world_position;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let normal = normalize(input.normal);
    let view_dir = normalize(scene.cameraPosition.xyz - input.world_position);

    var color = vec3<f32>(0.0);
    for (var i = 0u; i < lights.count; i++) {
        let light = lights.lights[i];
        let light_dir = lightDirection(light, input.world_position);
        let radiance = evaluateLight(light, normal, view_dir, input.world_position,
            material.color.rgb, material.metallic, material.roughness);
        color += radiance * shadowFactor(i, input.world_position, light_dir, normal);
    }
    color += material.emissive * material.color.rgb;

    return vec4<f32>(tonemap(color), material.color.a);
}
