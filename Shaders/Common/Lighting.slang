#language slang 2026;
// Shared lighting library: the light buffer the engine binds to group 0
// binding 1, plus the BRDF functions every lit shader reuses. Lit shaders
// call Lighting::EvaluateAll, so the light/shadow loop exists in exactly one
// place.
module Lighting;

import Shadows;

public struct LightData
{
    float4 position_type;    // xyz = world position, w = 0 directional / 1 point / 2 spot
    float4 color_intensity;  // rgb = color, w = intensity
    float4 direction_range;  // xyz = direction, w = range
};

public struct LightsUniform
{
    // Explicit uint padding (a vec3 pad would 16-align past offset 16 and
    // shift the array to 32); the engine packs count + 3 padders then the
    // array at offset 16.
    uint count;
    uint pad0;
    uint pad1;
    uint pad2;
    LightData lights[8];
};

[[vk::binding(1, 0)]] public ConstantBuffer<LightsUniform> lights;

public struct Lighting
{
    static float3 FresnelSchlick(float cosine, float3 f0)
    {
        return f0 + (float3(1.0) - f0) * pow(1.0 - cosine, 5.0);
    }

    static float DistributionGgx(float3 n, float3 h, float roughness)
    {
        let a = roughness * roughness;
        let a2 = a * a;
        let ndoth = max(dot(n, h), 0.0);
        let denominator = ndoth * ndoth * (a2 - 1.0) + 1.0;
        return a2 / max(3.14159265 * denominator * denominator, 0.0001);
    }

    static float GeometrySchlick(float cosine, float roughness)
    {
        let k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
        return cosine / max(cosine * (1.0 - k) + k, 0.0001);
    }

    static float3 Direction(LightData light, float3 surfacePosition)
    {
        if (light.position_type.w > 0.5)
        {
            return normalize(light.position_type.xyz - surfacePosition);
        }
        // The buffer stores the direction the light travels; the incident
        // direction a surface sees is the opposite.
        return normalize(-light.direction_range.xyz);
    }

    static float3 Evaluate(LightData light, float3 normal, float3 viewDir, float3 surfacePosition, float3 baseColor, float metallic, float roughness)
    {
        let lightDir = Direction(light, surfacePosition);
        let ndotl = max(dot(normal, lightDir), 0.0);
        if (ndotl <= 0.0)
        {
            return float3(0.0);
        }

        let halfVector = normalize(lightDir + viewDir);
        let ndotv = max(dot(normal, viewDir), 0.0);
        let hdotv = max(dot(halfVector, viewDir), 0.0);
        let f0 = lerp(float3(0.04), baseColor, metallic);
        let fresnel = FresnelSchlick(hdotv, f0);
        let specular = DistributionGgx(normal, halfVector, roughness)
            * GeometrySchlick(ndotl, roughness) * GeometrySchlick(ndotv, roughness)
            * fresnel / max(4.0 * ndotl * ndotv, 0.001);
        let diffuse = (float3(1.0) - fresnel) * (1.0 - metallic) * baseColor / 3.14159265;

        var attenuation = 1.0;
        if (light.position_type.w > 0.5)
        {
            let toLight = light.position_type.xyz - surfacePosition;
            let dist = length(toLight);
            let range = light.direction_range.w;
            attenuation = clamp(1.0 - dist / max(range, 0.0001), 0.0, 1.0);
            attenuation = attenuation * attenuation;
            if (light.position_type.w > 1.5)
            {
                let fromLight = normalize(surfacePosition - light.position_type.xyz);
                let coneCosine = dot(normalize(light.direction_range.xyz), fromLight);
                let outerCosine = cos(radians(22.5));
                let innerCosine = cos(radians(12.5));
                attenuation *= smoothstep(outerCosine, innerCosine, coneCosine);
            }
        }

        return (diffuse + specular) * ndotl * light.color_intensity.rgb
            * (light.position_type.w > 1.5 ? 1.0 : light.color_intensity.w) * attenuation;
    }

    // Evaluates every enabled light at the surface, shadows included. This is
    // the one loop lit shaders call: the light index iterated here is exactly
    // the index Shadow::Factor uses for its per-light metadata, so the arrays
    // can never drift out of sync.
    static float3 EvaluateAll(float3 worldPosition, float3 normal, float3 viewDir, float3 baseColor, float metallic, float roughness)
    {
        var color = float3(0.0);
        for (uint i = 0; i < lights.count; i++)
        {
            let light = lights.lights[i];
            let light_dir = Direction(light, worldPosition);
            let radiance = Evaluate(light, normal, viewDir, worldPosition, baseColor, metallic, roughness);
            color += radiance * Shadow::Factor(i, worldPosition, light_dir, normal);
        }
        return color;
    }
};
