#language slang 2026;
// Full glTF metallic-roughness PBR material with the standard texture set
// (albedo, normal, metallic-roughness, occlusion, emissive).
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
// The object's transform in the previous frame, for per-object motion vectors.
[[vk::binding(8, 1)]] ConstantBuffer<float4x4> previousModel;

[shader("vertex")]
VertexOutput vs_main(VertexInput input)
{
    return VertexTransform::ToClip(input, model, previousModel);
}

[shader("fragment")]
FragmentOutput fs_main(VertexOutput input)
{
    // Resolve the surface from the glTF texture set (textures are sRGB and
    // hardware-decoded to linear; metallic lives in the blue channel,
    // roughness in the green). Opacity is the base-color factor alpha times the
    // albedo texture alpha, so BLEND materials follow the texture's alpha.
    Material m = Material::Init(input);
    let albedo = albedoTexture.Sample(materialSampler, input.uv);
    m.BaseColor = albedo.rgb * material.color.rgb;
    m.Normal = Material::NormalFromMap(input, normalTexture.Sample(materialSampler, input.uv).xyz);
    let metallic_roughness = metallicRoughnessTexture.Sample(materialSampler, input.uv);
    m.Metallic = material.metallic * metallic_roughness.b;
    m.Roughness = material.roughness * metallic_roughness.g;
    m.Occlusion = occlusionTexture.Sample(materialSampler, input.uv).r * material.occlusion;
    m.Emissive = emissiveTexture.Sample(materialSampler, input.uv).rgb * material.emissive;
    m.Opacity = material.color.a * albedo.a;

    FragmentOutput output;
    output.color = ShadingModelStandard::Shade(m);
    output.velocity = Velocity::Compute(input);
    return output;
}
