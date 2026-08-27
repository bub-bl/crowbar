#language slang 2026;
// Shared shadow-mapping library. The engine renders shadow-casting lights
// into a single 2D depth atlas (one 512px tile per face) and binds it here to
// group 0 bindings 2-4, alongside the per-light shadow metadata written by the
// renderer (Lighting owns the light array itself). Lit shaders multiply their
// per-light radiance by Shadow::Factor.
module Shadows;

public struct ShadowFace
{
    float4 uvRect;      // atlas UV rect of this face: xy = top-left, zw = bottom-right
    float4x4 viewProj;  // world -> light clip space
};

public struct ShadowLight
{
    float4 flags;       // x = enabled, y = type (0 directional / 1 point), z = face count, w = bias
    ShadowFace faces[6];
};

public struct ShadowUniforms
{
    float4 atlasSize;   // xy = atlas dimensions in texels, z = slope bias scale
    ShadowLight lights[8];
};

[[vk::binding(2, 0)]] public ConstantBuffer<ShadowUniforms> shadows;
[[vk::binding(3, 0)]] public DepthTexture2D shadowMap;
[[vk::binding(4, 0)]] public SamplerComparisonState shadowSampler;

public struct Shadow
{
    // Selects the face explicitly so every read is constant-indexed (some naga
    // builds are stricter about dynamic indexing into struct-member arrays).
    static ShadowFace Face(ShadowLight data, uint index)
    {
        if (index == 0u) return data.faces[0];
        if (index == 1u) return data.faces[1];
        if (index == 2u) return data.faces[2];
        if (index == 3u) return data.faces[3];
        if (index == 4u) return data.faces[4];
        return data.faces[5];
    }

    // 3x3 percentage-closer filtering of one face. `lightDir` is the normalized
    // direction from the surface toward the light (already computed by the BRDF).
    static float SampleFace(ShadowFace face, float3 ndc, float bias)
    {
        // Outside the face frustum: the light is not occluded here.
        if (ndc.x < -1.0 || ndc.x > 1.0 || ndc.y < -1.0 || ndc.y > 1.0)
        {
            return 1.0;
        }

        // WebGPU framebuffer origin is top-left, so NDC +y maps to v = 0.
        let uv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
        let uvSize = face.uvRect.zw - face.uvRect.xy;
        let atlasUv = face.uvRect.xy + uv * uvSize;
        let depthRef = clamp(ndc.z - bias, 0.0, 1.0);

        // One atlas texel is constant across all faces because a face's UV span
        // scales exactly with its texel count.
        let texel = float2(1.0) / shadows.atlasSize.xy;

        var visibility = 0.0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                let offset = float2(float(dx), float(dy)) * texel;
                visibility += shadowMap.SampleCmp(shadowSampler, atlasUv + offset, depthRef);
            }
        }
        return visibility / 9.0;
    }

    // Compute-pass variant for volumetric fog. Depth comparison samplers are
    // not available on every backend in compute shaders, so fog samples the
    // depth value with a regular sampler and performs the comparison itself.
    static float SampleFaceLinear(ShadowFace face, float3 ndc, float bias, SamplerState sampler)
    {
        if (ndc.x < -1.0 || ndc.x > 1.0 || ndc.y < -1.0 || ndc.y > 1.0)
            return 1.0;

        let uv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
        let uvSize = face.uvRect.zw - face.uvRect.xy;
        let atlasUv = face.uvRect.xy + uv * uvSize;
        let depthRef = clamp(ndc.z - bias, 0.0, 1.0);
        let texel = float2(1.0) / shadows.atlasSize.xy;

        var visibility = 0.0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                let offset = float2(float(dx), float(dy)) * texel;
                let mapDepth = shadowMap.SampleLevel(sampler, atlasUv + offset, 0.0);
                visibility += mapDepth + 0.00001 >= depthRef ? 1.0 : 0.0;
            }
        }
        return visibility / 9.0;
    }

    static float FactorLinear(uint lightIndex, float3 worldPosition, float3 lightDir, SamplerState sampler)
    {
        let data = shadows.lights[lightIndex];
        if (data.flags.x < 0.5)
            return 1.0;

        let bias = data.flags.w;
        if (data.flags.y < 0.5)
        {
            let face = data.faces[0];
            let clip = mul(face.viewProj, float4(worldPosition, 1.0));
            return SampleFaceLinear(face, clip.xyz / max(clip.w, 0.000001), bias, sampler);
        }

        let d = normalize(-lightDir);
        let ax = abs(d.x);
        let ay = abs(d.y);
        let az = abs(d.z);
        var index = 4u;
        if (ax >= ay && ax >= az)
            index = d.x < 0.0 ? 0u : 1u;
        else if (ay >= az)
            index = d.y < 0.0 ? 2u : 3u;
        else
            index = d.z < 0.0 ? 4u : 5u;

        let face = Face(data, index);
        let clip = mul(face.viewProj, float4(worldPosition, 1.0));
        return SampleFaceLinear(face, clip.xyz / max(clip.w, 0.000001), bias, sampler);
    }

    static float Factor(uint lightIndex, float3 worldPosition, float3 lightDir, float3 normal)
    {
        let data = shadows.lights[lightIndex];
        if (data.flags.x < 0.5)
        {
            return 1.0;
        }

        // Slope-scaled bias: a surface angled toward the light changes depth fast
        // across the shadow map, so a constant bias aliases into a texel-sized acne
        // lattice (visible as "lines" on floors and walls). Scale the bias by how
        // parallel the surface is to the light (|dot| -> 0) on top of the per-light
        // constant floor, which stays for surfaces facing the light directly.
        let bias = data.flags.w + shadows.atlasSize.z * (1.0 - abs(dot(normal, lightDir)));
        if (data.flags.y < 0.5)
        {
            // Directional light: one orthographic face.
            let face = data.faces[0];
            let clip = mul(face.viewProj, float4(worldPosition, 1.0));
            return SampleFace(face, clip.xyz / max(clip.w, 0.000001), bias);
        }

        // Point light: pick the cube face the surface lies in. `lightDir` points
        // from the surface toward the light (Lighting.slang), so the direction from
        // the light toward the surface is its negation.
        let d = normalize(-lightDir);
        let ax = abs(d.x);
        let ay = abs(d.y);
        let az = abs(d.z);
        var index = 4u;
        if (ax >= ay && ax >= az)
        {
            index = d.x < 0.0 ? 0u : 1u;
        }
        else if (ay >= az)
        {
            index = d.y < 0.0 ? 2u : 3u;
        }
        else
        {
            index = d.z < 0.0 ? 4u : 5u;
        }

        let face = Face(data, index);
        let clip = mul(face.viewProj, float4(worldPosition, 1.0));
        return SampleFace(face, clip.xyz / max(clip.w, 0.000001), bias);
    }
};
