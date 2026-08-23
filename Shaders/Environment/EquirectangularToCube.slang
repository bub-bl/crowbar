#language slang 2026;
import Cubemap;

struct ConvertUniforms { uint face; uint size; uint2 padding; };
[[vk::binding(0, 0)]] Texture2D sourceTexture;
[[vk::binding(1, 0)]] SamplerState sourceSampler;
[[vk::binding(2, 0)]] RWTexture2D<float4> outputTexture;
[[vk::binding(3, 0)]] ConstantBuffer<ConvertUniforms> parameters;

[shader("compute")]
[numthreads(8, 8, 1)]
void cs_main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= parameters.size || id.y >= parameters.size) return;
    let direction = Cubemap::Direction(parameters.face, (float2(id.xy) + 0.5) / float(parameters.size));
    let uv = float2(atan2(direction.z, direction.x) / 6.28318530718 + 0.5,
                    0.5 - asin(clamp(direction.y, -1.0, 1.0)) / 3.14159265359);
    outputTexture[id.xy] = sourceTexture.SampleLevel(sourceSampler, uv, 0.0);
}
