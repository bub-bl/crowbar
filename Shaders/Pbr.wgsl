#include "Common/Transform.wgsl"
#include "Common/Lighting.wgsl"

struct MaterialUniforms {
    color: vec4<f32>,
    metallic: f32,
    roughness: f32,
    occlusion: f32,
    emissive: f32,
};

@group(1) @binding(0) var<uniform> model: mat4x4<f32>;
@group(1) @binding(1) var<uniform> material: MaterialUniforms;
@group(1) @binding(2) var materialSampler: sampler;
@group(1) @binding(3) var albedoTexture: texture_2d<f32>;
@group(1) @binding(4) var normalTexture: texture_2d<f32>;
@group(1) @binding(5) var metallicRoughnessTexture: texture_2d<f32>;
@group(1) @binding(6) var occlusionTexture: texture_2d<f32>;
@group(1) @binding(7) var emissiveTexture: texture_2d<f32>;

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
    @location(2) tangent: vec4<f32>,
    @location(3) uv: vec2<f32>,
};

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    let world_position = model * vec4<f32>(input.position, 1.0);
    output.world_position = world_position.xyz;

    let normal_matrix = mat3x3<f32>(model[0].xyz, model[1].xyz, model[2].xyz);
    output.normal = normalize(normal_matrix * input.normal);
    output.tangent = vec4<f32>(normalize(normal_matrix * input.tangent.xyz), input.tangent.w);
    output.uv = input.uv;
    output.clip_position = scene.proj * scene.view * world_position;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    // Textures are sRGB (hardware decodes to linear); the maps use glTF
    // conventions: metallic in the blue channel, roughness in the green.
    let albedo = textureSample(albedoTexture, materialSampler, input.uv).rgb * material.color.rgb;
    let normal_map = textureSample(normalTexture, materialSampler, input.uv).xyz * 2.0 - 1.0;
    let metallic_roughness = textureSample(metallicRoughnessTexture, materialSampler, input.uv);
    let occlusion = textureSample(occlusionTexture, materialSampler, input.uv).r;
    let emissive = textureSample(emissiveTexture, materialSampler, input.uv).rgb;

    // TBN from the interpolated normal/tangent plus the flat-blue default
    // normal map (0.5, 0.5, 1.0), which reconstructs the geometric normal.
    let geometric_normal = normalize(input.normal);
    let tangent = normalize(input.tangent.xyz - geometric_normal * dot(geometric_normal, input.tangent.xyz));
    let bitangent = normalize(cross(geometric_normal, tangent) * input.tangent.w);
    let normal = normalize(tangent * normal_map.x + bitangent * normal_map.y + geometric_normal * normal_map.z);
    let view_dir = normalize(scene.cameraPosition.xyz - input.world_position);

    var color = vec3<f32>(0.0);
    for (var i = 0u; i < lights.count; i++) {
        color += evaluateLight(lights.lights[i], normal, view_dir, input.world_position,
            albedo, material.metallic * metallic_roughness.b, material.roughness * metallic_roughness.g);
    }
    color = color * occlusion * material.occlusion + emissive * material.emissive;

    return vec4<f32>(tonemap(color), material.color.a);
}
