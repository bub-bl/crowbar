#include "Common/Transform.slang"
#include "Common/VertexIO.slang"
#include "Common/Lighting.slang"

// Material parameters, packed by the engine from this struct's fields.
struct MaterialUniforms
{
    float4 color;
    float metallic;
    float roughness;
    float emissive;
};

[[vk::binding(0, 1)]] ConstantBuffer<float4x4> model;
[[vk::binding(1, 1)]] ConstantBuffer<MaterialUniforms> material;

[shader("vertex")]
VertexOutput vs_main(VertexInput input)
{
    VertexOutput output;
    let world_position = mul(model, float4(input.position, 1.0));
    output.world_position = world_position.xyz;

    let normal_matrix = (float3x3)model;
    output.normal = normalize(mul(normal_matrix, input.normal));
    output.clip_position = mul(mul(scene.proj, scene.view), world_position);
    return output;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    let normal = normalize(input.normal);
    let view_dir = normalize(scene.cameraPosition.xyz - input.world_position);

    var color = evaluateLights(input.world_position, normal, view_dir,
        material.color.rgb, material.metallic, material.roughness);
    color += material.emissive * material.color.rgb;

    return float4(tonemap(color), material.color.a);
}
