#language slang 2026;
// Integrates the accumulated froxel volume toward the camera. The accumulate
// pass stores per-froxel in-scattered radiance (RGB) and extinction (A).
// Dispatched once per slice: each thread marches the accumulated slices from
// the near plane up to the dispatch's slice, transporting scattered light and
// transmittance with Beer's law, and writes the accumulated
//   RGB = integrated in-scattered radiance between the camera and this slice
//   A   = transmittance along the same span
// The apply pass then samples this at a pixel's eye distance to blend the fog.
import Fog;

float NoiseHash(float3 value)
{
    return frac(sin(dot(value, float3(12.9898, 78.233, 37.719))) * 43758.5453);
}

struct FogIntegrateUniforms
{
    float4 fogParams;     // x = scattered strength, y = extinction strength, z = unused, w = slice count
    float4 depths;        // x = near, y = draw distance, z = slice thickness, w = current slice index
    float4 volumeParams;  // x = unused, y = froxel width, z = froxel height, w = unused
};

[[vk::binding(0, 0)]] ConstantBuffer<FogIntegrateUniforms> fog;
[[vk::binding(0, 1)]] Texture2DArray<float4> scatteringVolume;     // accumulate output
[[vk::binding(1, 1)]] SamplerState volumeSampler;
[[vk::binding(2, 1)]] RWTexture2D<float4> outIntegrated;

[shader("compute")]
[numthreads(8, 8, 1)]
void cs_main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= uint(fog.volumeParams.y) || id.y >= uint(fog.volumeParams.z)) return;

    let sliceCount = int(fog.fogParams.w);
    let target = int(fog.depths.w);
    let u = (float(id.x) + 0.5) / fog.volumeParams.y;
    let v = (float(id.y) + 0.5) / fog.volumeParams.z;
    let uv = float2(u, v);

    var scattered = float3(0.0);
    var transmittance = 1.0;
    for (int s = 0; s <= target && s < sliceCount; s++)
    {
        let sampleUv = float2(
            (float(id.x) + 0.5) / fog.volumeParams.y,
            (float(id.y) + 0.5) / fog.volumeParams.z);
        let accum = scatteringVolume.SampleLevel(volumeSampler, float3(sampleUv, float(s)), 0.0);
        let inScatter = accum.rgb;
        let sigmaT = accum.a;
        let stepLength = Fog::SliceThickness(s, fog.depths.x, fog.depths.y, sliceCount);
        let sliceJitter = (NoiseHash(float3(id.xy, s)) - 0.5) * 0.15;
        let filteredStepLength = stepLength * (1.0 + sliceJitter);
        let stepT = exp(-sigmaT * filteredStepLength);
        // Accumulate the analytic single-scattering solution for this slice.
        // Accumulate.rgb is a source term (sigmaS * incomingLight), so it must
        // be divided by extinction before multiplying by (1 - T). Without this
        // factor the scene darkens while the fog contributes almost no light.
        let sliceScatter = sigmaT > 1e-5
            ? inScatter / sigmaT * (1.0 - stepT)
            : inScatter * filteredStepLength;
        // Snapshot the fog accumulated between the camera and this slice, then
        // extend the march past this slice.
        if (s == target)
        {
            outIntegrated[id.xy] = float4(scattered, transmittance);
            return;
        }
        scattered = scattered * stepT + sliceScatter;
        transmittance *= stepT;
    }

    outIntegrated[id.xy] = float4(scattered, transmittance);
}