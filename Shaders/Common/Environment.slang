#language slang 2026;
module Environment;

public struct EnvironmentUniforms
{
    float4 parameters; // x = rotation radians, y = intensity, z = exposure, w = maximum specular mip
    float4 tint;
    float4 provider; // x = 1 for procedural atmosphere, 0 for cubemap/neutral
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

    static float3 SampleSky(float3 direction)
    {
        let rotated = Rotate(direction);
        if (environment.provider.x > 0.5)
        {
            let horizon = float3(0.52, 0.68, 0.92);
            let zenith = float3(0.08, 0.22, 0.55);
            let sunColor = float3(1.0, 0.72, 0.42);
            let altitude = clamp(rotated.y * 0.5 + 0.5, 0.0, 1.0);
            let sky = lerp(horizon, zenith, pow(altitude, 0.65));
            let sunDirection = normalize(float3(-0.35, 0.8, -0.2));
            let sun = pow(max(dot(rotated, sunDirection), 0.0), 128.0);
            return ApplySettings(sky + sunColor * sun * 2.0);
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
