#language slang 2026;
// Full glTF metallic-roughness PBR material with the standard texture set
// (albedo, normal, metallic-roughness, occlusion, emissive).
import Vertex;
import VertexTransform;
import Material;
import ShadingModelStandard;

struct MaterialUniforms
{
    float4 color;
    float metallic;
    float roughness;
    float occlusion;
    float emissive;
};

[[vk::binding(0, 1)]] ConstantBuffer<float4x4> model;
[[vk::binding(1, 1)]] ConstantBuffer<MaterialUniforms> material;
[[vk::binding(2, 1)]] SamplerState materialSampler;
[[vk::binding(3, 1)]] Texture2D albedoTexture;
[[vk::binding(4, 1)]] Texture2D normalTexture;
[[vk::binding(5, 1)]] Texture2D metallicRoughnessTexture;
[[vk::binding(6, 1)]] Texture2D occlusionTexture;
[[vk::binding(7, 1)]] Texture2D emissiveTexture;

[shader("vertex")]
VertexOutput vs_main(VertexInput input)
{
    return VertexTransform::ToClip(input, model);
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    // Resolve the surface from the glTF texture set (textures are sRGB and
    // hardware-decoded to linear; metallic lives in the blue channel,
    // roughness in the green).
    Material m = Material::Init(input);
    m.BaseColor = albedoTexture.Sample(materialSampler, input.uv).rgb * material.color.rgb;
    m.Normal = Material::NormalFromMap(input, normalTexture.Sample(materialSampler, input.uv).xyz);
    let metallic_roughness = metallicRoughnessTexture.Sample(materialSampler, input.uv);
    m.Metallic = material.metallic * metallic_roughness.b;
    m.Roughness = material.roughness * metallic_roughness.g;
    m.Occlusion = occlusionTexture.Sample(materialSampler, input.uv).r * material.occlusion;
    m.Emissive = emissiveTexture.Sample(materialSampler, input.uv).rgb * material.emissive;
    m.Opacity = material.color.a;

    return ShadingModelStandard::Shade(m);
}
