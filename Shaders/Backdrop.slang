// GPU backdrop-filter compositor, mirroring S&box's ui_backdropfilter shader.
// One instanced fullscreen quad is drawn per backdrop-filter region between the
// 3D scene blit and the UI overlay: the fragment samples the scene texture
// (the WebGPU 3D viewport), blurs it with a radial multi-tap kernel, applies
// the CSS color transforms in declaration order, overlays the panel's own
// background color, and writes the result inside the rounded border box.
//
// This is what keeps the frame rate up: the CPU never sees the 3D scene and
// the UI raster only re-runs when the UI itself changes, so moving the
// camera costs nothing on the UI side.

struct BackdropParams
{
    float4 region;     // xy = border-box top-left in screen px, zw = size px
    float4 uvRect;     // normalized scene-texture coords of the region (u0, v0, u1, v1)
    float4 radiusBlur; // x = corner radius px, y = blur sigma px, z = panel alpha, w = unused
    float4 tint;       // the panel's background color (straight alpha), painted over the backdrop
    float4 ops[8];     // x = op type, y = amount; op types: 0 brightness, 1 contrast,
                       // 2 saturate, 3 invert, 4 hue-rotate (deg), 5 sepia, 6 opacity
    float4 opCount;    // x = number of active ops
};

[[vk::binding(0, 0)]] Texture2D scene;
[[vk::binding(1, 0)]] SamplerState scene_sampler;
[[vk::binding(2, 0)]] StructuredBuffer<BackdropParams> params;

struct VertexOutput
{
    float4 position : SV_Position;
    // The instance index selects the region's params; this wgpu build does not
    // expose instance_index in the fragment stage, so it is forwarded from the
    // vertex shader as a flat varying.
    [[vk::location(1)]] nointerpolation uint instance : TEXCOORD1;
};

[shader("vertex")]
VertexOutput vs_main([[vk::location(0)]] float2 position : POSITION, [[vk::location(1)]] float2 uv : TEXCOORD0,
                     uint instanceIndex : SV_InstanceID)
{
    VertexOutput out;
    out.position = float4(position, 0.0, 1.0);
    out.instance = instanceIndex;
    return out;
}

float4 applyOp(float4 color, float4 op)
{
    var c = color;
    let opType = uint(op.x);
    let amount = op.y;
    if (opType == 0u)
    {
        // brightness
        c = float4(c.rgb * amount, c.a);
    }
    else if (opType == 1u)
    {
        // contrast
        c = float4(clamp((c.rgb - float3(0.5)) * amount + float3(0.5), float3(0.0), float3(1.0)), c.a);
    }
    else if (opType == 2u)
    {
        // saturate
        let luma = dot(c.rgb, float3(0.2126, 0.7152, 0.0722));
        c = float4(lerp(float3(luma), c.rgb, amount), c.a);
    }
    else if (opType == 3u)
    {
        // invert
        c = float4(lerp(c.rgb, float3(1.0) - c.rgb, amount), c.a);
    }
    else if (opType == 4u)
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
    else if (opType == 5u)
    {
        // sepia (amount 0..1, 1 = full sepia)
        let m = 1.0 - amount;
        let r = c.r;
        let g = c.g;
        let b = c.b;
        c.r = (0.393 + 0.607 * m) * r + (0.769 - 0.769 * m) * g + (0.189 - 0.189 * m) * b;
        c.g = (0.349 - 0.349 * m) * r + (0.686 + 0.314 * m) * g + (0.168 - 0.168 * m) * b;
        c.b = (0.272 - 0.272 * m) * r + (0.534 - 0.534 * m) * g + (0.131 + 0.869 * m) * b;
    }
    else if (opType == 6u)
    {
        // opacity
        c.a *= amount;
    }
    return c;
}

// The ops array is indexed with constant indices (naga rejects dynamic
// indices into struct-member arrays); the runtime count gates each step.
float4 applyOps(float4 color, BackdropParams p)
{
    var c = color;
    let count = uint(p.opCount.x);
    if (count > 0u) { c = applyOp(c, p.ops[0]); }
    if (count > 1u) { c = applyOp(c, p.ops[1]); }
    if (count > 2u) { c = applyOp(c, p.ops[2]); }
    if (count > 3u) { c = applyOp(c, p.ops[3]); }
    if (count > 4u) { c = applyOp(c, p.ops[4]); }
    if (count > 5u) { c = applyOp(c, p.ops[5]); }
    if (count > 6u) { c = applyOp(c, p.ops[6]); }
    if (count > 7u) { c = applyOp(c, p.ops[7]); }
    return c;
}

struct FragmentInput
{
    [[vk::location(1)]] nointerpolation uint instance : TEXCOORD1;
};

[shader("fragment")]
float4 fs_main(float4 frag : SV_Position, FragmentInput input) : SV_Target
{
    let p = params[input.instance];

    // Normalized position inside the border box, then the matching scene uv.
    // uvRect holds (u0, v0, u1, v1), so the span is the difference — the
    // mapping is identity: uv.x = frag.x / sceneWidth, uv.y = frag.y / height,
    // which keeps the backdrop content aligned with the sharp scene.
    let rel = (frag.xy - p.region.xy) / p.region.zw;
    let uv = p.uvRect.xy + rel * (p.uvRect.zw - p.uvRect.xy);

    // Rounded-rectangle signed distance (iquilezles.org/articles/distfunctions2d)
    // for the border-box mask with one pixel of antialiasing.
    let half = p.region.zw * 0.5;
    let center = p.region.xy + half;
    let radius = min(p.radiusBlur.x, min(half.x, half.y));
    let q = abs(frag.xy - center) - (half - float2(radius));
    let sd = length(max(q, float2(0.0))) + min(max(q.x, q.y), 0.0) - radius;
    let mask = 1.0 - smoothstep(-1.0, 0.0, sd);

    // Radial multi-tap blur of the scene (16 directions x 3 steps), applied
    // directly on the source texture like S&box's ui_backdropfilter.
    var color = scene.SampleLevel(scene_sampler, uv, 0.0);
    let blurSigma = p.radiusBlur.y;
    if (blurSigma > 0.0)
    {
        uint width, height;
        scene.GetDimensions(width, height);
        let size = float2(blurSigma) / float2(width, height);
        for (uint d = 0; d < 16u; d++)
        {
            let angle = float(d) * 6.28318530718 / 16.0;
            let dir = float2(cos(angle), sin(angle));
            for (uint j = 1; j <= 3u; j++)
            {
                let t = float(j) / 3.0;
                color += scene.SampleLevel(scene_sampler, uv + dir * size * t, 0.0);
            }
        }
        color /= 49.0;
    }

    color = applyOps(color, p);

    // The panel's own background color paints over the filtered backdrop.
    color = float4(lerp(color.rgb, p.tint.rgb, p.tint.a), color.a);

    return float4(color.rgb, mask * color.a * p.radiusBlur.z);
}
