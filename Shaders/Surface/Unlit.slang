#language slang 2026;
// Unlit material: forwards the base color with no lighting. It pulls in only
// the unlit shading model, so the pass has no light/shadow bindings.
import Vertex;
import VertexTransform;
import Material;
import ShadingModelUnlit;
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
    m.Opacity = material.color.a;

    FragmentOutput output;
    output.color = ShadingModelUnlit::Shade(m);
    output.velocity = Velocity::Compute(input);
    return output;
}
