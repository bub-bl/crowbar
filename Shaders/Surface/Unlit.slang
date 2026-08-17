#language slang 2026;
// Unlit material: forwards the base color with no lighting. It pulls in only
// the unlit shading model, so the pass has no light/shadow bindings.
import Vertex;
import VertexTransform;
import Material;
import ShadingModelUnlit;

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
    return VertexTransform::ToClip(input, model);
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    Material m = Material::Init(input);
    m.BaseColor = material.color.rgb;
    m.Opacity = material.color.a;

    return ShadingModelUnlit::Shade(m);
}
