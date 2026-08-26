#language slang 2026;
// Standard lit material (the engine's fallback): flat base color with
// metallic/roughness/emissive parameters, no textures.
import Vertex;
import VertexTransform;
import Material;
import ShadingModelStandard;
import Velocity;

struct MaterialUniforms
{
    float4 color;
    float metallic;
    float roughness;
    float emissive;
};

[[vk::binding(0, 1)]] ConstantBuffer<float4x4> model;
[[vk::binding(1, 1)]] ConstantBuffer<MaterialUniforms> material;
// The object's transform in the previous frame, for per-object motion vectors.
[[vk::binding(2, 1)]] ConstantBuffer<float4x4> previousModel;

[shader("vertex")]
VertexOutput vs_main(VertexInput input)
{
    return VertexTransform::ToClip(input, model, previousModel);
}

[shader("fragment")]
FragmentOutput fs_main(VertexOutput input)
{
    Material m = Material::Init(input);
    m.BaseColor = material.color.rgb;
    m.Metallic = material.metallic;
    m.Roughness = material.roughness;
    m.Emissive = material.emissive * material.color.rgb;
    m.Opacity = material.color.a;

    FragmentOutput output;
    output.color = ShadingModelStandard::Shade(m);
    output.velocity = Velocity::Compute(input);
    return output;
}
