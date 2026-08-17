#language slang 2026;
// Standard lit material (the engine's fallback): flat base color with
// metallic/roughness/emissive parameters, no textures.
import Vertex;
import VertexTransform;
import Material;
import ShadingModelStandard;

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
    m.Metallic = material.metallic;
    m.Roughness = material.roughness;
    m.Emissive = material.emissive * material.color.rgb;
    m.Opacity = material.color.a;

    return ShadingModelStandard::Shade(m);
}
