#language slang 2026;
// Depth-of-field prefilter: downsamples the scene to half resolution while
// computing a per-pixel circle-of-confusion (CoC) disk radius. The CoC is kept
// SIGNED: positive behind the focal plane (far), negative in front (near), so
// the two blur layers that follow each gather only their own side of focus.
// Following the UE/Unity convention, a 2x2 block collapses by keeping the most
// out-of-focus tap (largest |CoC|), which lets bokeh disks grow to their true
// radius and stops color from bleeding across a focal edge.

struct DepthOfFieldUniforms
{
    float4 lens; // focus distance, focal length mm, f-number, sensor height mm
    float2 depthRange;
    float2 viewportSize;
    float maxBlurRadius;
    float3 padding;
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<DepthOfFieldUniforms> postProcess;
[[vk::binding(3, 0)]] DepthTexture2D sceneDepth;

struct VertexOutput { float4 position : SV_Position; [[vk::location(0)]] float2 uv : TEXCOORD0; };

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

// Signed disk radius in HALF-RES pixels (full-res pixels / 2), clamped so the
// full-res radius never exceeds maxBlurRadius. Positive = far, negative = near.
static float CircleOfConfusionRadius(float depth, DepthOfFieldUniforms settings)
{
    let focalLength = settings.lens.y * 0.001;
    let apertureDiameter = focalLength / settings.lens.z;
    let sensorHeight = settings.lens.w * 0.001;
    let cocSensor = apertureDiameter * focalLength * (depth - settings.lens.x) /
        max(depth * (settings.lens.x - focalLength), 1e-5);
    let cocFullPx = cocSensor * settings.viewportSize.y / sensorHeight;
    return clamp(cocFullPx * 0.5, -settings.maxBlurRadius * 0.5, settings.maxBlurRadius * 0.5);
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    let size = max(postProcess.viewportSize, float2(1.0));
    let invTexel = float2(1.0) / size;

    var bestColor = float3(0.0);
    var bestCoc = 0.0;
    var bestMagnitude = -1.0;
    for (uint k = 0u; k < 4u; k++)
    {
        let corner = float2(float(k & 1u), float(k >> 1u));
        let sampleUV = input.uv + (corner - 0.5) * invTexel;
        let pixel = int2(clamp(sampleUV * size, float2(0.0), size - 1.0));
        let depth = LinearDepth(sceneDepth.Load(int3(pixel, 0)), postProcess.depthRange.x, postProcess.depthRange.y);
        let coc = CircleOfConfusionRadius(depth, postProcess);
        let color = sceneTexture.Sample(sceneSampler, sampleUV).rgb;

        // Keep the most out-of-focus tap so disks grow to their true radius.
        if (abs(coc) > bestMagnitude)
        {
            bestMagnitude = abs(coc);
            bestCoc = coc;
            bestColor = color;
        }
    }

    return float4(bestColor, bestCoc);
}