#language slang 2026;
// Fullscreen apply pass for volumetric fog: samples the integrated froxel
// volume (a 2D array of slices, each slice a plane at a view depth) at a
// pixel's eye distance, then blends the scene toward the accumulated scattering
// by the accumulated transmittance:
//   final = scene.rgb * transmittance + integratedScattering
// The eye distance is reconstructed from the scene depth with the same linear
// depth mapping used by the accumulate / integrate passes, so the fog lines up
// with geometry. Surfaces beyond the fog draw distance are fully fogged and
// far-away points receive the fog accumulated up to the draw distance.
import Fog;

struct FogApplyUniforms
{
    float4 depths;       // x = near, y = draw distance, z = slice count, w = faded draw distance
    float4 params;       // x = strength, y = froxel width, z = froxel height, w = unused
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] Texture2DArray<float4> fogVolume;
[[vk::binding(3, 0)]] Texture2D<float> sceneDepth;
[[vk::binding(4, 0)]] ConstantBuffer<FogApplyUniforms> fog;

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[shader("vertex")]
VertexOutput vs_main(uint vertexId : SV_VertexID)
{
    let clip = float2(float((vertexId << 1u) & 2u), float(vertexId & 2u)) * 2.0 - 1.0;
    VertexOutput output;
    output.position = float4(clip, 0.0, 1.0);
    output.uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    return output;
}

static float LinearDepth(float rawDepth, float nearPlane, float farPlane)
{
    return nearPlane * farPlane / max(farPlane - rawDepth * (farPlane - nearPlane), 1e-5);
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    let scene = sceneTexture.Sample(sceneSampler, input.uv).rgb;
    let rawDepth = sceneDepth.Sample(sceneSampler, input.uv);
    let viewDepth = LinearDepth(rawDepth, fog.depths.x, fog.depths.y);

    let sliceF = Fog::SliceCoordinate(viewDepth, fog.depths.x, fog.depths.y, int(fog.depths.z));
    // Rgba32Float is not filterable, so interpolate the two nearest slices by
    // hand rather than relying on a trilinear sampler. Pixel coords come from the
    // UV and the froxel grid size.
    let sliceLow = clamp(int(floor(sliceF)), 0, int(fog.depths.z) - 1);
    let sliceHigh = min(sliceLow + 1, int(fog.depths.z) - 1);
    let alpha = clamp(sliceF - float(sliceLow), 0.0, 1.0);
    let px = min(int(input.uv.x * fog.params.y), int(fog.params.y) - 1);
    let py = min(int(input.uv.y * fog.params.z), int(fog.params.z) - 1);
    let low = fogVolume.Load(int4(px, py, sliceLow, 0));
    let high = fogVolume.Load(int4(px, py, sliceHigh, 0));
    let integrated = lerp(low, high, alpha);

    var color = scene * integrated.a + integrated.rgb * fog.params.x;
    return float4(color, 1.0);
}