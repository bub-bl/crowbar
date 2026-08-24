#language slang 2026;
module Environment;

import Atmosphere;

public struct EnvironmentUniforms
{
    float4 parameters; // x = rotation radians, y = intensity, z = exposure, w = maximum specular mip
    float4 tint;
    float4 provider; // x = 1 for procedural atmosphere, 0 for cubemap/neutral
    float4 sun; // xyz = sun direction (world space), w = sun angular radius (radians)
    float4 atmosphere; // x = turbidity, y = ground albedo, z = sun intensity, w = unused
};

[[vk::binding(0, 2)]] public TextureCube environmentMap;
[[vk::binding(1, 2)]] public TextureCube irradianceMap;
[[vk::binding(2, 2)]] public TextureCube prefilteredSpecularMap;
[[vk::binding(3, 2)]] public Texture2D brdfLut;
[[vk::binding(4, 2)]] public SamplerState environmentSampler;
[[vk::binding(5, 2)]] public ConstantBuffer<EnvironmentUniforms> environment;

public struct EnvironmentLighting
{
    static float3 Rotate(float3 direction)
    {
        let angle = environment.parameters.x;
        let sine = sin(angle);
        let cosine = cos(angle);
        return float3(
            cosine * direction.x - sine * direction.z,
            direction.y,
            sine * direction.x + cosine * direction.z);
    }

    static float3 ApplySettings(float3 color)
    {
        return color * environment.tint.rgb * environment.parameters.y * exp2(environment.parameters.z);
    }

    static float3 SampleSky(float3 direction, float cameraHeight)
    {
        let rotated = Rotate(direction);
        if (environment.provider.x > 0.5)
        {
            AtmosphereParams p;
            p.sunDirection = environment.sun.xyz;
            p.sunAngularRadius = environment.sun.w;
            p.turbidity = environment.atmosphere.x;
            p.groundAlbedo = environment.atmosphere.y;
            p.sunIntensity = environment.atmosphere.z;
            return ApplySettings(Atmosphere::EvaluateSky(rotated, p, cameraHeight));
        }
        return ApplySettings(environmentMap.SampleLevel(environmentSampler, rotated, 0.0).rgb);
    }

    static float3 EvaluateIbl(
        float3 normal,
        float3 viewDirection,
        float3 baseColor,
        float metallic,
        float roughness,
        float ao)
    {
        let n = normalize(normal);
        let v = normalize(viewDirection);
        let ndotv = max(dot(n, v), 0.0);
        let f0 = lerp(float3(0.04), baseColor, metallic);
        let fresnel = f0 + (max(float3(1.0 - roughness), f0) - f0) * pow(1.0 - ndotv, 5.0);

        let diffuse = irradianceMap.SampleLevel(environmentSampler, Rotate(n), 0.0).rgb
            * baseColor * (1.0 - metallic);
        let reflection = reflect(-v, n);
        let specularSample = prefilteredSpecularMap.SampleLevel(
            environmentSampler,
            Rotate(reflection),
            roughness * environment.parameters.w).rgb;
        let brdf = brdfLut.SampleLevel(environmentSampler, float2(ndotv, roughness), 0.0).rg;
        let specular = specularSample * (fresnel * brdf.x + brdf.y);
        return ApplySettings((diffuse + specular) * ao);
    }
};
