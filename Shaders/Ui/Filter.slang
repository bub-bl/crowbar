#language slang 2026;
import Screen;
import FilterOps;

// GPU CSS-filter compositor (Crowbar 2D UI backend).
//
// A filtered subtree is rendered to an offscreen target; this pass composites
// it back onto its parent by sampling the target with a radial multi-tap blur
// and applying the color filter operations in declaration order. The ops
// mirror FilterOpKind in Filter2D.cs (0 blur, then 1..8 in FilterOps.slang).

struct FilterParams
{
    float4 blur;          // x = blur radius (px)
    float4 ops[8];        // x = op type, y = amount
    float4 opCount;       // x = number of ops
};

[[vk::binding(0, 0)]] Texture2D source;
[[vk::binding(1, 0)]] SamplerState source_sampler;
[[vk::binding(2, 0)]] StructuredBuffer<FilterParams> params;

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[shader("vertex")]
VertexOutput vs_main([[vk::location(0)]] float2 position : POSITION, [[vk::location(1)]] float2 uv : TEXCOORD0)
{
    VertexOutput out;
    out.position = float4(Screen::UnitToClip(position), 0.0, 1.0);
    out.uv = uv;
    return out;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    var color = source.Sample(source_sampler, input.uv);

    // Radial multi-tap blur of the source (16 directions x 3 steps), matching
    // Backdrop.slang. Blur is applied before the color ops.
    let blurSigma = params[0].blur.x;
    if (blurSigma > 0.0)
    {
        uint width, height;
        source.GetDimensions(width, height);
        let size = float2(blurSigma) / float2(width, height);
        for (uint d = 0; d < 16u; d++)
        {
            let angle = float(d) * 6.28318530718 / 16.0;
            let dir = float2(cos(angle), sin(angle));
            for (uint j = 1; j <= 3u; j++)
            {
                let t = float(j) / 3.0;
                color += source.Sample(source_sampler, input.uv + dir * size * t);
            }
        }
        color /= 49.0;
    }

    return FilterOps::ApplyAll(color, params[0].ops, params[0].opCount.x);
}
