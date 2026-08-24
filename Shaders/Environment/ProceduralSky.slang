#language slang 2026;
// Bakes the analytic atmosphere into one face of the environment cubemap so
// PBR reflections/irradiance match the procedural sky. The sun direction is
// in world space (the environment rotation is applied when sampling, exactly
// as in EnvironmentLighting::SampleSky), and the sky is evaluated at ground
// level.
import Atmosphere;
import Cubemap;

struct SkyUniforms
{
    uint face;
    uint size;
    uint2 padding;
    float4 sun;        // xyz = sun direction (world space), w = sun angular radius (radians)
    float4 atmosphere; // x = turbidity, y = ground albedo, z = sun intensity, w = unused
};

[[vk::binding(0, 0)]] RWTexture2D<float4> outputTexture;
[[vk::binding(1, 0)]] ConstantBuffer<SkyUniforms> parameters;

[shader("compute")]
[numthreads(8, 8, 1)]
void cs_main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= parameters.size || id.y >= parameters.size) return;
    let direction = Cubemap::Direction(parameters.face, (float2(id.xy) + 0.5) / float(parameters.size));
    AtmosphereParams p;
    p.sunDirection = parameters.sun.xyz;
    p.sunAngularRadius = parameters.sun.w;
    p.turbidity = parameters.atmosphere.x;
    p.groundAlbedo = parameters.atmosphere.y;
    p.sunIntensity = parameters.atmosphere.z;
    outputTexture[id.xy] = float4(Atmosphere::EvaluateSky(direction, p, 0.0), 1.0);
}
