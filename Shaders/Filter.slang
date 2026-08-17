// GPU CSS-filter compositor (Crowbar 2D UI backend).
//
// A filtered subtree is rendered to an offscreen target; this pass composites
// it back onto its parent by sampling the target with a radial multi-tap blur
// and applying the color filter operations in declaration order. The ops
// mirror Backdrop.slang's applyOp with grayscale added; the operation types
// match FilterOpKind in Filter2D.cs (0 blur, 1 brightness, 2 contrast,
// 3 grayscale, 4 hue-rotate, 5 invert, 6 opacity, 7 saturate, 8 sepia).

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
    out.position = float4(position.x * 2.0 - 1.0, 1.0 - position.y * 2.0, 0.0, 1.0);
    out.uv = uv;
    return out;
}

float4 applyOp(float4 c, float4 op)
{
    var color = c;
    let opType = uint(op.x);
    let amount = op.y;
    if (opType == 1u)
    {
        // brightness
        color = float4(color.rgb * amount, color.a);
    }
    else if (opType == 2u)
    {
        // contrast
        color = float4(clamp((color.rgb - float3(0.5)) * amount + float3(0.5), float3(0.0), float3(1.0)), color.a);
    }
    else if (opType == 3u)
    {
        // grayscale
        let luma = dot(color.rgb, float3(0.2126, 0.7152, 0.0722));
        color = float4(lerp(color.rgb, float3(luma), clamp(amount, 0.0, 1.0)), color.a);
    }
    else if (opType == 4u)
    {
        // hue-rotate (degrees): the canonical CSS Filter Effects matrix.
        let rad = amount * 3.14159265358979 / 180.0;
        let cosA = cos(rad);
        let sinA = sin(rad);
        let r = color.r;
        let g = color.g;
        let b = color.b;
        color.r = (0.213 + cosA * 0.787 - sinA * 0.213) * r
            + (0.715 - cosA * 0.715 - sinA * 0.715) * g
            + (0.072 - cosA * 0.072 + sinA * 0.928) * b;
        color.g = (0.213 - cosA * 0.213 + sinA * 0.143) * r
            + (0.715 + cosA * 0.285 + sinA * 0.140) * g
            + (0.072 - cosA * 0.072 - sinA * 0.283) * b;
        color.b = (0.213 - cosA * 0.213 - sinA * 0.787) * r
            + (0.715 - cosA * 0.715 + sinA * 0.715) * g
            + (0.072 + cosA * 0.928 + sinA * 0.072) * b;
    }
    else if (opType == 5u)
    {
        // invert
        color = float4(lerp(color.rgb, float3(1.0) - color.rgb, clamp(amount, 0.0, 1.0)), color.a);
    }
    else if (opType == 6u)
    {
        // opacity
        color.a *= clamp(amount, 0.0, 1.0);
    }
    else if (opType == 7u)
    {
        // saturate
        let luma = dot(color.rgb, float3(0.2126, 0.7152, 0.0722));
        color = float4(lerp(float3(luma), color.rgb, amount), color.a);
    }
    else if (opType == 8u)
    {
        // sepia (amount 0..1, 1 = full sepia)
        let s = clamp(amount, 0.0, 1.0);
        let m = 1.0 - s;
        let r = color.r;
        let g = color.g;
        let b = color.b;
        color.r = (0.393 + 0.607 * m) * r + (0.769 - 0.769 * m) * g + (0.189 - 0.189 * m) * b;
        color.g = (0.349 - 0.349 * m) * r + (0.686 + 0.314 * m) * g + (0.168 - 0.168 * m) * b;
        color.b = (0.272 - 0.272 * m) * r + (0.534 - 0.534 * m) * g + (0.131 + 0.869 * m) * b;
    }
    return color;
}

float4 applyOps(float4 c, FilterParams p)
{
    var color = c;
    let count = p.opCount.x;
    if (count > 0.0) { color = applyOp(color, p.ops[0]); }
    if (count > 1.0) { color = applyOp(color, p.ops[1]); }
    if (count > 2.0) { color = applyOp(color, p.ops[2]); }
    if (count > 3.0) { color = applyOp(color, p.ops[3]); }
    if (count > 4.0) { color = applyOp(color, p.ops[4]); }
    if (count > 5.0) { color = applyOp(color, p.ops[5]); }
    if (count > 6.0) { color = applyOp(color, p.ops[6]); }
    if (count > 7.0) { color = applyOp(color, p.ops[7]); }
    return color;
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

    return applyOps(color, params[0]);
}
