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
    float4 depths;       // x = near, y = camera far, z = draw distance, w = slice count
    float4 viewport;     // x = scene width, y = scene height, z = froxel width, w = froxel height
    float4 params;       // x = strength, yzw = unused
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] Texture2DArray<float4> fogVolume;
[[vk::binding(3, 0)]] DepthTexture2D sceneDepth;
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
    let size = max(fog.viewport.xy, float2(1.0));
    let scenePx = int2(clamp(input.uv * size, float2(0.0), size - float2(1.0)));
    let rawDepth = sceneDepth.Load(int3(scenePx, 0));
    let viewDepth = LinearDepth(rawDepth, fog.depths.x, fog.depths.y);

    let sliceCount = int(fog.depths.w);
    let sliceF = Fog::SliceCoordinate(viewDepth, fog.depths.x, fog.depths.z, sliceCount);
    // The froxel volume is RGBA16F so it is filterable, but nearest-load the two
    // surrounding slices and lerp to keep the lookup cheap and explicit.
    let sliceLow = clamp(int(floor(sliceF)), 0, sliceCount - 1);
    let sliceHigh = min(sliceLow + 1, sliceCount - 1);
    let alpha = clamp(sliceF - float(sliceLow), 0.0, 1.0);
    let fsize = max(fog.viewport.zw, float2(1.0));
    let px = min(int(input.uv.x * fsize.x), int(fsize.x) - 1);
    let py = min(int(input.uv.y * fsize.y), int(fsize.y) - 1);
    let low = fogVolume.Load(int4(px, py, sliceLow, 0));
    let high = fogVolume.Load(int4(px, py, sliceHigh, 0));
    let integrated = lerp(low, high, alpha);

    // Integrated alpha is transmittance. Do not multiply the scene by a
    // second fog strength here: that makes ordinary density values erase the
    // entire image and produces flat black/white walls instead of haze.
    var color = scene * integrated.a + integrated.rgb;
    return float4(color, 1.0);
}