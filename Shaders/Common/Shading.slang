// The reusable PBR shading API, modelled on s&box's Material +
// ShadingModelStandard. A lit shader resolves its surface into a Material and
// hands it to ShadingModelStandard::Shade, which owns all direct lighting,
// shadows, ambient occlusion, emission and tonemapping. Shaders differ only in
// how they sample the material — the lighting is written exactly once, here.
//
// Includes VertexTransform.slang (rather than VertexIO.slang directly) so a
// shader can include both this file and VertexTransform.slang without pulling
// the shared vertex structs in twice.

#include "Common/VertexTransform.slang"
#include "Common/Lighting.slang"

// Resolved surface data passed from the material-sampling stage to a shading
// model. All colors are linear; positions and normals are world-space.
struct Material
{
    float3 WorldPosition;   // world-space position of the shaded fragment
    float3 BaseColor;       // linear albedo
    float3 Normal;          // world-space normal (geometric or normal-mapped)
    float Metallic;         // 0 dielectric .. 1 metal
    float Roughness;        // perceptual roughness (0 smooth .. 1 rough)
    float Occlusion;        // ambient-occlusion multiplier
    float3 Emissive;        // emission added after lighting
    float Opacity;          // alpha written to the render target

    // s&box-style constructor: starts from the interpolated vertex data with
    // neutral defaults, ready for a shader to fill in its own sampled values.
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
};

// Reconstructs a world-space normal from the interpolated tangent frame and a
// tangent-space normal map (0..1 encoding; flat-blue (0.5, 0.5, 1.0) yields
// the geometric normal).
float3 normalFromMap(VertexOutput input, float3 normalMap)
{
    let geometric_normal = normalize(input.normal);
    let tangent = normalize(input.tangent.xyz - geometric_normal * dot(geometric_normal, input.tangent.xyz));
    let bitangent = normalize(cross(geometric_normal, tangent) * input.tangent.w);
    let sample = normalMap * 2.0 - 1.0;
    return normalize(tangent * sample.x + bitangent * sample.y + geometric_normal * sample.z);
}

// Standard PBR shading model: direct lights + shadows + AO + emission, then
// tonemapping, in one s&box-style entry point.
struct ShadingModelStandard
{
    static float4 Shade(Material m)
    {
        let view_dir = normalize(scene.cameraPosition.xyz - m.WorldPosition);
        var color = evaluateLights(m.WorldPosition, m.Normal, view_dir,
            m.BaseColor, m.Metallic, m.Roughness);
        color = color * m.Occlusion + m.Emissive;
        return float4(tonemap(color), m.Opacity);
    }
};
