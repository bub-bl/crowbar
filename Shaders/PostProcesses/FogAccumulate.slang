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
    float4 position_type;    // xyz = world position, w = 0 directional / 1 point / 2 spot
    float4 color_intensity;  // rgb = color, w = intensity
    float4 direction_range;  // xyz = travel direction or spot axis, w = range
};

struct LightsFogUniform
{
    uint count;
    uint pad0;
    uint pad1;
    uint pad2;
    LightData lights[8];
};

// Per-light volumetric-fog controls, aligned by light index against the shared
// light buffer. x = fog scattering multiplier (0 disables the light in fog),
// yzw = fog-specific tint.
struct LightFogData
{
    float4 params;
};

struct FogVolumeData
{
    float4 center;   // xyz = world-space center, w = unused
    float4 extent;   // xyz = half extents, w = unused
    float4 params;   // x = strength, y = falloff exponent, z = color R, w = color G
    float4 color;    // x = color B, y = noise scale, z = noise strength, w = reserved
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
[[vk::binding(6, 0)]] StructuredBuffer<LightFogData> fogLightData;
[[vk::binding(0, 1)]] StructuredBuffer<FogVolumeData> volumeBuffer;
[[vk::binding(1, 1)]] RWTexture2D<float4> outScattering;

float Hash3(float3 position)
{
    return frac(sin(dot(position, float3(127.1, 311.7, 74.7))) * 43758.5453);
}

float Noise3D(float3 position)
{
    let cell = floor(position);
    let cellFraction = frac(position);
    let smoothLocal = cellFraction * cellFraction * (3.0 - 2.0 * cellFraction);

    let n000 = Hash3(cell + float3(0.0, 0.0, 0.0));
    let n100 = Hash3(cell + float3(1.0, 0.0, 0.0));
    let n010 = Hash3(cell + float3(0.0, 1.0, 0.0));
    let n110 = Hash3(cell + float3(1.0, 1.0, 0.0));
    let n001 = Hash3(cell + float3(0.0, 0.0, 1.0));
    let n101 = Hash3(cell + float3(1.0, 0.0, 1.0));
    let n011 = Hash3(cell + float3(0.0, 1.0, 1.0));
    let n111 = Hash3(cell + float3(1.0, 1.0, 1.0));

    let low = lerp(lerp(n000, n100, smoothLocal.x), lerp(n010, n110, smoothLocal.x), smoothLocal.y);
    let high = lerp(lerp(n001, n101, smoothLocal.x), lerp(n011, n111, smoothLocal.x), smoothLocal.y);
    return lerp(low, high, smoothLocal.z);
}

float3 WorldPositionAtSlice(uint2 id, float depth)
{
    let u = (float(id.x) + 0.5) / fog.volumeParams.y;
    let v = (float(id.y) + 0.5) / fog.volumeParams.z;
    // Fragment UVs are top-left origin while NDC +Y is the top of the frame.
    // Keep this convention identical to FogApply's fullscreen UV mapping;
    // using v*2-1 mirrors the volume vertically and makes it appear to move
    // when the camera pitches.
    let ndc = float2(u * 2.0 - 1.0, 1.0 - v * 2.0);
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
            let edgeDensity = pow(max(1.0 - normalizedDistance, 0.0), vol.params.y);
            // Stable world-space 3D noise breaks up a uniform screen tint and
            // makes the finite box read as a participating medium. The world
            // position keeps the pattern fixed while the camera moves.
            let noiseScale = max(vol.color.y, 0.0);
            let noiseStrength = clamp(vol.color.z, 0.0, 1.0);
            let densityNoise = lerp(1.0 - noiseStrength, 1.0, Noise3D(worldPos * noiseScale));
            density += vol.params.x * edgeDensity * densityNoise;
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
    for (uint l = 0; l < min(fogLights.count, 8u); l++)
    {
        let light = fogLights.lights[l];

        var lightDir = float3(0.0);
        var attenuation = 1.0;
        if (light.position_type.w > 0.5)
        {
            let toLight = light.position_type.xyz - worldPos;
            let dist = length(toLight);
            lightDir = toLight / max(dist, 0.0001);
            let range = max(light.direction_range.w, 0.0001);
            let rangeAttenuation = clamp(1.0 - dist / range, 0.0, 1.0);
            attenuation = rangeAttenuation * rangeAttenuation;

            if (light.position_type.w > 1.5)
            {
                // The light direction points from the source into the cone.
                // A fog sample is lit only when it lies between the inner and
                // outer cone boundaries, just like a surface sample.
                let fromLight = normalize(worldPos - light.position_type.xyz);
                let coneCosine = dot(normalize(light.direction_range.xyz), fromLight);
                let outerCosine = cos(radians(22.5));
                let innerCosine = cos(radians(12.5));
                let coneAttenuation = smoothstep(outerCosine, innerCosine, coneCosine);
                attenuation *= coneAttenuation;
            }
        }
        else
        {
            lightDir = normalize(-light.direction_range.xyz);
        }

        let phase = Fog::PhaseHenveyGreenstein(dot(-lightDir, eyeDir), fog.fogParams.y);
        // A volume has no surface normal; use the light direction only to keep
        // the shadow bias stable while testing whether geometry blocks the light
        // before it reaches this froxel.
        let visibility = Shadow::FactorVolume(l, worldPos, fogShadowSampler);
        // Per-light fog control: a multiplier (0 hides the light in fog) and an
        // independent fog tint.
        let fogControl = fogLightData[l].params.x;
        let fogTint = fogLightData[l].params.yzw;
        inScatter += light.color_intensity.rgb * light.color_intensity.w
            * fogTint * phase * tintedSigmaS * attenuation * visibility * fogControl;
    }

    // Ambient fill so fully shadowed fog still reads as airy volume instead of black.
    inScatter += tintedSigmaS * fog.fade.z;

    outScattering[id.xy] = float4(inScatter, sigmaT);
}