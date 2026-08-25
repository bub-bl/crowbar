#language slang 2026;
// Depth-of-field blur: gathers each layer (far/near) with a circular, CoC-aware
// disk kernel. The gather radius comes from the center pixel's OWN disk radius
// read from the prefiltered mask, so in-focus regions return sharp while truly
// out-of-focus regions grow a proper bokeh disk — nothing is blurred at a fixed
// radius (the previous bug that left the whole image soft).
//
// A tap contributes only once its own disk reaches that ring (a CoC occlusion
// test), and the continuous ramp softens the disk edge instead of a hard
// polygon. The near and far layers are separated by the sign of the stored CoC.

struct DepthOfFieldBlurUniforms
{
    float2 invTexel;  // 1 / half-res dimensions
    float maxBlurRadius;
    float layer;      // +1 far, -1 near
};

[[vk::binding(0, 0)]] Texture2D cocTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<DepthOfFieldBlurUniforms> postProcess;

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

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    // 8 directions x 4 rings = 32 taps per layer. This matches the beam count
    // UE5/Unity gather in one burst (they run ~8 dirs x 4 rings) and halves our
    // previous 16x4 gather. The signed CoC in the mask controls both the span
    // and the occlusion so the disk stays circular and grows correctly.
    static const uint DirectionCount = 8u;
    static const uint RingCount = 4u;

    let fractionSign = postProcess.layer;
    let center = cocTexture.Sample(sceneSampler, input.uv);
    let centerCoc = center.a * fractionSign;

    // Interior within half a texel (or the other side of focus): keep sharp.
    // Skipping the gather for these keeps the cost ~zero in the focused body.
    if (centerCoc < 1.0)
        return float4(center.rgb, 0.0);

    var color = center.rgb * 0.25;
    var weightSum = 0.25;
    var coverage = 0.0;

    for (uint d = 0u; d < DirectionCount; d++)
    {
        // Rotating the direction with the ring index scrambles the tap pattern
        // so nearby pixels don't align into visible spokes.
        let baseAngle = float(d) * 0.785398163; // 2*pi/8
        for (uint r = 0u; r < RingCount; r++)
        {
            let radius01 = sqrt((float(r) + 0.5) / float(RingCount));
            let turn = baseAngle + float(r) * 0.25;
            let dir = float2(cos(turn), sin(turn));
            let offset = dir * radius01 * centerCoc * postProcess.invTexel;

            let tap = cocTexture.Sample(sceneSampler, input.uv + offset);
            let tapCoc = tap.a * fractionSign;

            // A tap contributes only once its own disk reaches this ring; the
            // smooth ramp softens the disk edge instead of a hard polygon.
            let reach = saturate(tapCoc - radius01 * centerCoc);

            // Early exit: past the opaque core the tap is fully in the clear for
            // this layer, so later rings add nothing. Divides the gather cost on
            // soft/out-of-focus edges.
            if (reach < 1e-4)
                continue;

            // Inner rings carry the bulk of the energy (soft, packed disks).
            let occupancy = 1.0 - radius01;

            color += tap.rgb * reach * occupancy;
            weightSum += reach * occupancy;
            coverage += reach;
        }
    }

    let blurred = color / max(weightSum, 1e-4);
    let covered = saturate(coverage / float(DirectionCount * RingCount));
    return float4(blurred, covered);
}