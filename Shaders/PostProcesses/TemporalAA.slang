#language slang 2026;
// Temporal anti-aliasing: reprojects the previous frame's history through the
// screen-space motion vectors, blends it with the current frame and rejects
// disoccluded/outlier history via a min/max neighborhood clamp to avoid
// ghosting. The output is written to both the chain target and the persistent
// history buffer, which the next frame reads back.

struct TAAUniforms
{
    float feedback;         // history blend weight in [0, 1]; higher = smoother
    float pad;
    float2 viewportSize;    // render resolution in pixels
    float2 jitterOffset;     // this frame's sub-pixel projection jitter in pixels
    float2 padding;
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<TAAUniforms> postProcess;
[[vk::binding(3, 0)]] Texture2D historyTexture;
[[vk::binding(4, 0)]] SamplerState historySampler;
// Screen-space motion vectors (current UV - previous UV, real motion).
[[vk::binding(5, 0)]] Texture2D velocityTexture;

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

struct FragmentOutput
{
    // Target 0 = the chain output (accumulated color), target 1 = the history
    // write (same accumulated color, stored for next frame's reprojection).
    [[vk::location(0)]] float4 color : SV_Target;
    [[vk::location(1)]] float4 history : SV_Target1;
};

[shader("fragment")]
FragmentOutput fs_main(VertexOutput input)
{
    let color = sceneTexture.Sample(sceneSampler, input.uv);

    // The velocity buffer stores real (un-jittered) motion as current UV minus
    // previous UV, so the previous frame's sample location is current minus
    // velocity. blend the previous frame's history there with the current frame.
    let velocity = velocityTexture.Sample(sceneSampler, input.uv).xy;
    let historyUv = input.uv - velocity;

    let history = historyTexture.Sample(historySampler, historyUv);

    // Reject ghosting from disocclusion or moving objects: clamp the history to
    // the min/max of the current frame's neighborhood so a stale or offset
    // sample cannot drag a wrong color into the accumulation. The scene is
    // sampled on a jittered lattice; center the box on the un-jittered world
    // position (uv - jitter) so it bounds the same surface edge the history
    // was reprojected from.
    let texel = 1.0 / max(postProcess.viewportSize, float2(1.0));
    let jitterUv = postProcess.jitterOffset * texel;
    let boxCenter = input.uv - jitterUv;
    let c00 = sceneTexture.Sample(sceneSampler, boxCenter + float2(-texel.x, -texel.y));
    let c10 = sceneTexture.Sample(sceneSampler, boxCenter + float2(texel.x, -texel.y));
    let c01 = sceneTexture.Sample(sceneSampler, boxCenter + float2(-texel.x, texel.y));
    let c11 = sceneTexture.Sample(sceneSampler, boxCenter + float2(texel.x, texel.y));
    let c0 = sceneTexture.Sample(sceneSampler, boxCenter + float2(0.0, -texel.y));
    let c1 = sceneTexture.Sample(sceneSampler, boxCenter + float2(0.0, texel.y));
    let c2 = sceneTexture.Sample(sceneSampler, boxCenter + float2(-texel.x, 0.0));
    let c3 = sceneTexture.Sample(sceneSampler, boxCenter + float2(texel.x, 0.0));

    let minValue = min(c00, min(c10, min(c01, min(c11, min(c0, min(c1, min(c2, min(c3, color))))))));
    let maxValue = max(c00, max(c10, max(c01, max(c11, max(c0, max(c1, max(c2, max(c3, color))))))));
    let clampedHistory = clamp(history, minValue, maxValue);

    // A reprojected sample that lands outside the visible history (or points far
    // off-frame) has no valid accumulation to trust: fall back to the current
    // frame so disoccluded pixels do not smear stale color in from an edge.
    let inFrame = all(historyUv >= float2(-0.5) && historyUv <= float2(1.5));
    let feedback = inFrame ? clamp(postProcess.feedback, 0.0, 1.0) : 0.0;
    let accumulated = lerp(color, clampedHistory, feedback);

    FragmentOutput output;
    output.color = float4(accumulated.rgb, 1.0);
    output.history = float4(accumulated.rgb, 1.0);
    return output;
}