#include "Common/Shading.slang"

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
    return transformVertex(input, model);
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    Material m = Material::Init(input);
    m.BaseColor = material.color.rgb;
    m.Metallic = material.metallic;
    m.Roughness = material.roughness;
    m.Emissive = material.emissive * material.color.rgb;
    m.Opacity = material.color.a;

    return ShadingModelStandard::Shade(m);
}
