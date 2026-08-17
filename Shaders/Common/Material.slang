#language slang 2026;
// Resolved surface data passed from the material-sampling stage to a shading
// model, in the style of s&box. All colors are linear; positions and normals
// are world-space. A shader starts from Material::Init(input), fills in its
// sampled values, then hands the material to a shading model's Shade().
module Material;

import Vertex;

public struct Material
{
    float3 WorldPosition;   // world-space position of the shaded fragment
    float3 BaseColor;       // linear albedo
    float3 Normal;          // world-space normal (geometric or normal-mapped)
    float Metallic;         // 0 dielectric .. 1 metal
    float Roughness;        // perceptual roughness (0 smooth .. 1 rough)
    float Occlusion;        // ambient-occlusion multiplier
    float3 Emissive;        // emission added after lighting
    float Opacity;          // alpha written to the render target

    // Starts from the interpolated vertex data with neutral defaults, ready
    // for a shader to fill in its own sampled values.
    static Material Init(VertexOutput input)
    {
        Material m;
        m.WorldPosition = input.world_position;
        m.BaseColor = float3(1.0);
        m.Normal = normalize(input.normal);
        m.Metallic = 0.0;
        m.Roughness = 0.5;
        m.Occlusion = 1.0;
        m.Emissive = float3(0.0);
        m.Opacity = 1.0;
        return m;
    }

    // Reconstructs a world-space normal from the interpolated tangent frame
    // and a tangent-space normal map (0..1 encoding; flat-blue (0.5, 0.5, 1.0)
    // yields the geometric normal).
    static float3 NormalFromMap(VertexOutput input, float3 normalMap)
    {
        let geometric_normal = normalize(input.normal);
        let tangent = normalize(input.tangent.xyz - geometric_normal * dot(geometric_normal, input.tangent.xyz));
        let bitangent = normalize(cross(geometric_normal, tangent) * input.tangent.w);
        let sample = normalMap * 2.0 - 1.0;
        return normalize(tangent * sample.x + bitangent * sample.y + geometric_normal * sample.z);
    }
};
