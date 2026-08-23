#language slang 2026;
import Cubemap;
import ImportanceSampling;

struct FilterUniforms { uint face; uint size; float roughness; uint sampleCount; };
[[vk::binding(0, 0)]] TextureCube sourceTexture;
[[vk::binding(1, 0)]] SamplerState sourceSampler;
[[vk::binding(2, 0)]] RWTexture2D<float4> outputTexture;
[[vk::binding(3, 0)]] ConstantBuffer<FilterUniforms> parameters;

[shader("compute")]
[numthreads(8, 8, 1)]
void cs_main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= parameters.size || id.y >= parameters.size) return;
    let normal = Cubemap::Direction(parameters.face, (float2(id.xy) + 0.5) / float(parameters.size));
    let up = abs(normal.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
    let tangent = normalize(cross(up, normal));
    let bitangent = cross(normal, tangent);
    var sum = float3(0.0);
    var weight = 0.0;
    for (uint sampleIndex = 0; sampleIndex < parameters.sampleCount; sampleIndex++)
    {
        let xi = ImportanceSampling::Hammersley(sampleIndex, parameters.sampleCount);
        let phi = 6.28318530718 * xi.x;
        let cosine = sqrt(1.0 - xi.y);
        let sine = sqrt(xi.y);
        let local = float3(cos(phi) * sine, sin(phi) * sine, cosine);
        let direction = tangent * local.x + bitangent * local.y + normal * local.z;
        sum += sourceTexture.SampleLevel(sourceSampler, direction, 0.0).rgb * cosine;
        weight += cosine;
    }
    outputTexture[id.xy] = float4(3.14159265359 * sum / max(weight, 0.0001), 1.0);
}
