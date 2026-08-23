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
    let view = normal;
    var sum = float3(0.0);
    var weight = 0.0;
    for (uint sampleIndex = 0; sampleIndex < parameters.sampleCount; sampleIndex++)
    {
        let halfway = ImportanceSampling::Ggx(
            ImportanceSampling::Hammersley(sampleIndex, parameters.sampleCount), normal, parameters.roughness);
        let direction = normalize(2.0 * dot(view, halfway) * halfway - view);
        let ndotl = max(dot(normal, direction), 0.0);
        if (ndotl > 0.0)
        {
            sum += sourceTexture.SampleLevel(sourceSampler, direction, 0.0).rgb * ndotl;
            weight += ndotl;
        }
    }
    outputTexture[id.xy] = float4(sum / max(weight, 0.0001), 1.0);
}
