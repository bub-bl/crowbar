#language slang 2026;
// Depth-of-field far composite: blends the sharp scene toward the far-blurred
// layer with an amount driven by the per-pixel physical CoC radius. The amount
// ramps over the full max-blur range so the transition into soft focus is
// gradual and crisp in-focus regions stay untouched (the previous code snapped
// most defocus to "fully blurred", leaving the whole frame soft).

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
[[vk::binding(3, 0)]] Texture2D blurTexture;
[[vk::binding(4, 0)]] SamplerState blurSampler;
[[vk::binding(5, 0)]] DepthTexture2D sceneDepth;

struct VertexOutput { float4 position : SV_Position; [[vk::location(0)]] float2 uv : TEXCOORD0; };

[shader("vertex")]
VertexOutput vs_main(uint vertexId : SV_VertexID)
{
    let clip = float2(float((vertexId << 1u) & 2u), float(vertexId & 2u)) * 2.0 - 1.0;
    VertexOutput output; output.position = float4(clip, 0.0, 1.0);
    output.uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5); return output;
}

static float LinearDepth(float d, float n, float f) { return n * f / max(f - d * (f - n), 1e-5); }

// Signed disk radius in FULL-RES pixels, positive behind the focal plane.
static float Coc(float depth, DepthOfFieldUniforms s)
{
    let focalLength = s.lens.y * 0.001;
    let diameter = focalLength / s.lens.z;
    let sensor = s.lens.w * 0.001;
    let value = diameter * focalLength * (depth - s.lens.x) / max(depth * (s.lens.x - focalLength), 1e-5);
    return value * s.viewportSize.y / sensor;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    let size = max(postProcess.viewportSize, float2(1.0));
    let pixel = int2(clamp(input.uv * size, float2(0.0), size - 1.0));
    let depth = LinearDepth(sceneDepth.Load(int3(pixel, 0)), postProcess.depthRange.x, postProcess.depthRange.y);
    let farCoc = max(Coc(depth, postProcess), 0.0);
    let amount = smoothstep(0.0, 1.0, saturate(farCoc / max(postProcess.maxBlurRadius, 1e-4)));
    let sharp = sceneTexture.Sample(sceneSampler, input.uv).rgb;
    let blurred = blurTexture.Sample(blurSampler, input.uv).rgb;
    return float4(lerp(sharp, blurred, amount), 1.0);
}