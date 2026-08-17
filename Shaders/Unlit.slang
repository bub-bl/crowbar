#include "Common/VertexTransform.slang"

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
    return transformVertex(input, model);
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    return material.color;
}
