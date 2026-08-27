#language slang 2026;
// Accumulates per-froxel fog density and light in-scattering into one slice of
// the froxel volume. Dispatched once per slice (the "slice" is carried in the
// per-dispatch uniform buffer, since the engine binds each 2D-array layer
// through a separate 2D storage-texture view). Each thread reconstructs the
// world position at the slice's view depth, evaluates the density from the
// active fog volumes and the in-scattered light from every light
// (Henyey-Greenstein phase weighted by the volume's scattering coefficient),
// and writes:
//   RGB = total in-scattered radiance toward the eye (SigmaS * phase * irradiance summed)
//   A   = extinction coefficient SigmaT
// The integrate pass later transports this toward the camera.
//
// The light and shadow buffers use the same group-0 layout as the lit surface
// shaders. Fog samples the shadow atlas at each froxel so lights create shafts
// in the medium instead of merely tinting the final image.
import Fog;
import Shadows;

struct LightData
{
    float4 position_type;    // xyz = world position, w = 0 directional / 1 point
    float4 color_intensity;  // rgb = color, w = intensity
    float4 direction_range;  // xyz = direction the light travels (directional), w = range (point)
};

struct LightsFogUniform
{
    uint count;
    uint pad0;
    uint pad1;
    uint pad2;
    LightData lights[8];
};

struct FogVolumeData
{
    float4 center;   // xyz = world-space center, w = unused
    float4 extent;   // xyz = half extents, w = unused
    float4 params;   // x = strength, y = falloff exponent, z = color R, w = color G
    float4 color;    // x = color B, y = reserved, z = reserved, w = reserved
};

struct FogAccumulateUniforms
{
    float4 fogParams;     // x = scattering coefficient, y = anisotropy, z = density scale, w = slice count
    float4 depths;        // x = near, y = draw distance, z = tanHalfFovX, w = current slice index
    float4 volumeParams;  // x = volume count, y = froxel width, z = froxel height, w = tanHalfFovY
    float4 fade;          // x = fade-in start depth, y = fade-in end depth, z = ambient scatter scale, w = unused
    float4 cameraBasis;   // xyz = world-space camera position, w = unused
    float4 cameraRight;   // xyz = camera right (view-space +X), w = unused
    float4 cameraUp;      // xyz = camera up (view-space +Y), w = unused
    float4 cameraForward; // xyz = camera forward (view-space -Z), w = unused
};

[[vk::binding(0, 0)]] ConstantBuffer<LightsFogUniform> fogLights;
[[vk::binding(1, 0)]] ConstantBuffer<FogAccumulateUniforms> fog;
[[vk::binding(5, 0)]] SamplerState fogShadowSampler;
[[vk::binding(0, 1)]] StructuredBuffer<FogVolumeData> volumeBuffer;
[[vk::binding(1, 1)]] RWTexture2D<float4> outScattering;

float3 WorldPositionAtSlice(uint2 id, float depth)
{
    let u = (float(id.x) + 0.5) / fog.volumeParams.y;
    let v = (float(id.y) + 0.5) / fog.volumeParams.z;
    let ndc = float2(u * 2.0 - 1.0, v * 2.0 - 1.0);
    // `depth` is distance along the camera forward axis. Construct a normalized
    // ray first, then scale it so every froxel lies on the correct perspective
    // ray rather than on a sheared plane.
    let ray = normalize(fog.cameraForward.xyz
        + fog.cameraRight.xyz * (ndc.x * fog.depths.z)
        + fog.cameraUp.xyz * (ndc.y * fog.volumeParams.w));
    return fog.cameraBasis.xyz + ray * (depth / max(dot(ray, fog.cameraForward.xyz), 0.0001));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void cs_main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= uint(fog.volumeParams.y) || id.y >= uint(fog.volumeParams.z)) return;

    let slice = int(fog.depths.w);
    let depth = Fog::SliceDepth(slice, fog.depths.x, fog.depths.y, int(fog.fogParams.w));
    let worldPos = WorldPositionAtSlice(id.xy, depth);

    var density = 0.0;
    var volumeColor = float3(1.0, 1.0, 1.0);
    let count = min(fog.volumeParams.x, 8.0);
    for (uint i = 0; i < uint(count); i++)
    {
        let vol = volumeBuffer[i];
        let rel = abs(worldPos - vol.center.xyz) / max(vol.extent.xyz, float3(0.0001));
        let normalizedDistance = max(rel.x, max(rel.y, rel.z));
        if (normalizedDistance <= 1.0)
        {
            density += vol.params.x * pow(max(1.0 - normalizedDistance, 0.0), vol.params.y);
            volumeColor = float3(vol.params.z, vol.params.w, vol.color.x);
        }
    }

    // Finite fog volumes must remain visible when the camera is inside them.
    // The volume falloff already provides the local edge fade; applying the
    // global camera fade here would suppress small volumes entirely when their
    // bounds end before FadeInStart.
    let depthFade = smoothstep(fog.fade.x, max(fog.fade.y, fog.fade.x + 0.001), depth);
    let fadeFactor = density > 0.0 ? 1.0 : depthFade;
    let sigmaS = density * fadeFactor * fog.fogParams.x * fog.fogParams.z;
    let tintedSigmaS = sigmaS * volumeColor;
    // Extinction is density, not scattering. Keeping these separate prevents
    // a scattering value of 1 from turning a large volume into an opaque wall.
    let sigmaT = max(density, 0.0) * fadeFactor * max(fog.fogParams.z, 0.0);
    let eyeDir = normalize(fog.cameraBasis.xyz - worldPos);

    var inScatter = float3(0.0);
    for (uint l = 0; l < fogLights.count; l++)
    {
        let light = fogLights.lights[l];

        var lightDir = float3(0.0);
        var attenuation = 1.0;
        if (light.position_type.w > 0.5)
        {
            lightDir = normalize(light.position_type.xyz - worldPos);
            let dist = length(light.position_type.xyz - worldPos);
            let range = light.direction_range.w;
            attenuation = clamp(1.0 - dist / max(range, 0.0001), 0.0, 1.0);
            attenuation = attenuation * attenuation;
        }
        else
        {
            lightDir = normalize(-light.direction_range.xyz);
        }

        let phase = Fog::PhaseHenveyGreenstein(dot(lightDir, eyeDir), fog.fogParams.y);
        // A volume has no surface normal; use the light direction only to keep
        // the shadow bias stable while testing whether geometry blocks the light
        // before it reaches this froxel.
        let visibility = Shadow::FactorLinear(l, worldPos, lightDir, fogShadowSampler);
        inScatter += light.color_intensity.rgb * light.color_intensity.w
            * phase * tintedSigmaS * attenuation * visibility;
    }

    // Ambient fill so fully shadowed fog still reads as airy volume instead of black.
    inScatter += tintedSigmaS * fog.fade.z;

    outScattering[id.xy] = float4(inScatter, sigmaT);
}