#include "Common/Transform.slang"
#include "Common/VertexIO.slang"
#include "Common/Lighting.slang"

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
    VertexOutput output;
    let world_position = mul(model, float4(input.position, 1.0));
    output.world_position = world_position.xyz;

    let normal_matrix = (float3x3)model;
    output.normal = normalize(mul(normal_matrix, input.normal));
    output.tangent = float4(normalize(mul(normal_matrix, input.tangent.xyz)), input.tangent.w);
    output.uv = input.uv;
    output.clip_position = mul(mul(scene.proj, scene.view), world_position);
    return output;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    // Textures are sRGB (hardware decodes to linear); the maps use glTF
    // conventions: metallic in the blue channel, roughness in the green.
    let albedo = albedoTexture.Sample(materialSampler, input.uv).rgb * material.color.rgb;
    let normal_map = normalTexture.Sample(materialSampler, input.uv).xyz * 2.0 - 1.0;
    let metallic_roughness = metallicRoughnessTexture.Sample(materialSampler, input.uv);
    let occlusion = occlusionTexture.Sample(materialSampler, input.uv).r;
    let emissive = emissiveTexture.Sample(materialSampler, input.uv).rgb;

    // TBN from the interpolated normal/tangent plus the flat-blue default
    // normal map (0.5, 0.5, 1.0), which reconstructs the geometric normal.
    let geometric_normal = normalize(input.normal);
    let tangent = normalize(input.tangent.xyz - geometric_normal * dot(geometric_normal, input.tangent.xyz));
    let bitangent = normalize(cross(geometric_normal, tangent) * input.tangent.w);
    let normal = normalize(tangent * normal_map.x + bitangent * normal_map.y + geometric_normal * normal_map.z);
    let view_dir = normalize(scene.cameraPosition.xyz - input.world_position);

    var color = evaluateLights(input.world_position, normal, view_dir,
        albedo, material.metallic * metallic_roughness.b, material.roughness * metallic_roughness.g);
    color = color * occlusion * material.occlusion + emissive * material.emissive;

    return float4(tonemap(color), material.color.a);
}
