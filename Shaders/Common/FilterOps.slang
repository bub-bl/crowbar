#language slang 2026;
// The shared CSS filter color operations, used by the backdrop and filter
// compositors. The op kinds match FilterOpKind in Filter2D.cs (1 brightness,
// 2 contrast, 3 grayscale, 4 hue-rotate, 5 invert, 6 opacity, 7 saturate,
// 8 sepia); blur (0) is applied by each pass separately, before these ops.
module FilterOps;

import Color;

public struct FilterOps
{
    static float4 Apply(float4 color, float4 op)
    {
        var c = color;
        let kind = uint(op.x);
        let amount = op.y;
        if (kind == 1u)
        {
            // brightness
            c = float4(c.rgb * amount, c.a);
        }
        else if (kind == 2u)
        {
            // contrast
            c = float4(clamp((c.rgb - float3(0.5)) * amount + float3(0.5), float3(0.0), float3(1.0)), c.a);
        }
        else if (kind == 3u)
        {
            // grayscale
            c = float4(lerp(c.rgb, float3(Color::Luminance(c.rgb)), clamp(amount, 0.0, 1.0)), c.a);
        }
        else if (kind == 4u)
        {
            // hue-rotate (degrees): the canonical CSS Filter Effects matrix.
            let rad = amount * 3.14159265358979 / 180.0;
            let cosA = cos(rad);
            let sinA = sin(rad);
            let r = c.r;
            let g = c.g;
            let b = c.b;
            c.r = (0.213 + cosA * 0.787 - sinA * 0.213) * r
                + (0.715 - cosA * 0.715 - sinA * 0.715) * g
                + (0.072 - cosA * 0.072 + sinA * 0.928) * b;
            c.g = (0.213 - cosA * 0.213 + sinA * 0.143) * r
                + (0.715 + cosA * 0.285 + sinA * 0.140) * g
                + (0.072 - cosA * 0.072 - sinA * 0.283) * b;
            c.b = (0.213 - cosA * 0.213 - sinA * 0.787) * r
                + (0.715 - cosA * 0.715 + sinA * 0.715) * g
                + (0.072 + cosA * 0.928 + sinA * 0.072) * b;
        }
        else if (kind == 5u)
        {
            // invert
            c = float4(lerp(c.rgb, float3(1.0) - c.rgb, clamp(amount, 0.0, 1.0)), c.a);
        }
        else if (kind == 6u)
        {
            // opacity
            c.a *= clamp(amount, 0.0, 1.0);
        }
        else if (kind == 7u)
        {
            // saturate
            c = float4(lerp(float3(Color::Luminance(c.rgb)), c.rgb, amount), c.a);
        }
        else if (kind == 8u)
        {
            // sepia (amount 0..1, 1 = full sepia)
            let s = clamp(amount, 0.0, 1.0);
            let m = 1.0 - s;
            let r = c.r;
            let g = c.g;
            let b = c.b;
            c.r = (0.393 + 0.607 * m) * r + (0.769 - 0.769 * m) * g + (0.189 - 0.189 * m) * b;
            c.g = (0.349 - 0.349 * m) * r + (0.686 + 0.314 * m) * g + (0.168 - 0.168 * m) * b;
            c.b = (0.272 - 0.272 * m) * r + (0.534 - 0.534 * m) * g + (0.131 + 0.869 * m) * b;
        }
        return c;
    }

    // Applies all active ops in order. The ops array is indexed with constant
    // indices only (naga rejects dynamic indices into struct-member arrays);
    // the runtime count gates each step.
    static float4 ApplyAll(float4 color, float4 ops[8], float count)
    {
        var c = color;
        let n = uint(count);
        if (n > 0u) { c = Apply(c, ops[0]); }
        if (n > 1u) { c = Apply(c, ops[1]); }
        if (n > 2u) { c = Apply(c, ops[2]); }
        if (n > 3u) { c = Apply(c, ops[3]); }
        if (n > 4u) { c = Apply(c, ops[4]); }
        if (n > 5u) { c = Apply(c, ops[5]); }
        if (n > 6u) { c = Apply(c, ops[6]); }
        if (n > 7u) { c = Apply(c, ops[7]); }
        return c;
    }
};
