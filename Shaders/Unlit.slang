#include "Common/Transform.slang"
#include "Common/VertexIO.slang"

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
    output.clip_position = mul(mul(scene.proj, scene.view), world_position);
    return output;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    return material.color;
}
