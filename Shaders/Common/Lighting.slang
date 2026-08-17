// Shared lighting library: the light buffer the engine binds to group 0
// binding 1, the shadow-mapping bindings and sampling (Shadows.slang), plus
// the BRDF functions every lit shader reuses. This is the "class library" of
// the shader API — lit shaders include this one file and call
// evaluateLights(), so the light/shadow loop exists in exactly one place.

#include "Common/Shadows.slang"

struct LightData
{
    float4 position_type;    // xyz = world position, w = 0 directional / 1 point
    float4 color_intensity;  // rgb = color, w = intensity
    float4 direction_range;  // xyz = direction the light travels (directional), w = range (point)
};

struct LightsUniform
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

[[vk::binding(1, 0)]] ConstantBuffer<LightsUniform> lights;

float3 fresnelSchlick(float cosine, float3 f0)
{
    return f0 + (float3(1.0) - f0) * pow(1.0 - cosine, 5.0);
}

float distributionGgx(float3 n, float3 h, float roughness)
{
    let a = roughness * roughness;
    let a2 = a * a;
    let ndoth = max(dot(n, h), 0.0);
    let denominator = ndoth * ndoth * (a2 - 1.0) + 1.0;
    return a2 / max(3.14159265 * denominator * denominator, 0.0001);
}

float geometrySchlick(float cosine, float roughness)
{
    let k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
    return cosine / max(cosine * (1.0 - k) + k, 0.0001);
}

float3 lightDirection(LightData light, float3 surfacePosition)
{
    if (light.position_type.w > 0.5)
    {
        return normalize(light.position_type.xyz - surfacePosition);
    }
    // The buffer stores the direction the light travels; the incident
    // direction a surface sees is the opposite.
    return normalize(-light.direction_range.xyz);
}

float3 evaluateLight(LightData light, float3 normal, float3 viewDir, float3 surfacePosition, float3 baseColor, float metallic, float roughness)
{
    let lightDir = lightDirection(light, surfacePosition);
    let ndotl = max(dot(normal, lightDir), 0.0);
    if (ndotl <= 0.0)
    {
        return float3(0.0);
    }

    let halfVector = normalize(lightDir + viewDir);
    let ndotv = max(dot(normal, viewDir), 0.0);
    let hdotv = max(dot(halfVector, viewDir), 0.0);
    let f0 = lerp(float3(0.04), baseColor, metallic);
    let fresnel = fresnelSchlick(hdotv, f0);
    let specular = distributionGgx(normal, halfVector, roughness)
        * geometrySchlick(ndotl, roughness) * geometrySchlick(ndotv, roughness)
        * fresnel / max(4.0 * ndotl * ndotv, 0.001);
    let diffuse = (float3(1.0) - fresnel) * (1.0 - metallic) * baseColor / 3.14159265;

    var attenuation = 1.0;
    if (light.position_type.w > 0.5)
    {
        let dist = length(light.position_type.xyz - surfacePosition);
        let range = light.direction_range.w;
        attenuation = clamp(1.0 - dist / max(range, 0.0001), 0.0, 1.0);
        attenuation = attenuation * attenuation;
    }

    return (diffuse + specular) * ndotl * light.color_intensity.rgb * light.color_intensity.w * attenuation;
}

// Evaluates every enabled light at the surface, shadows included. This is the
// one loop lit shaders call: the light index iterated here is exactly the
// index shadowFactor() uses for its per-light metadata, so the arrays can
// never drift out of sync.
float3 evaluateLights(float3 worldPosition, float3 normal, float3 viewDir, float3 baseColor, float metallic, float roughness)
{
    var color = float3(0.0);
    for (uint i = 0; i < lights.count; i++)
    {
        let light = lights.lights[i];
        let light_dir = lightDirection(light, worldPosition);
        let radiance = evaluateLight(light, normal, viewDir, worldPosition, baseColor, metallic, roughness);
        color += radiance * shadowFactor(i, worldPosition, light_dir, normal);
    }
    return color;
}

float3 tonemap(float3 color)
{
    let mapped = color / (color + float3(1.0));
    return pow(mapped, float3(1.0 / 2.2));
}
