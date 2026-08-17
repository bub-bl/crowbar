#language slang 2026;
import Sdf;
import FilterOps;

// GPU backdrop-filter compositor, mirroring s&box's ui_backdropfilter shader.
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
    float4 ops[8];     // x = op type, y = amount (see FilterOps.slang)
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

    // Rounded-rectangle signed distance for the border-box mask, with one
    // pixel of antialiasing.
    let sd = Sdf::RoundedRect(frag.xy, p.region, p.radiusBlur.x);
    let mask = 1.0 - smoothstep(-1.0, 0.0, sd);

    // Radial multi-tap blur of the scene (16 directions x 3 steps), applied
    // directly on the source texture like s&box's ui_backdropfilter.
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

    color = FilterOps::ApplyAll(color, p.ops, p.opCount.x);

    // The panel's own background color paints over the filtered backdrop.
    color = float4(lerp(color.rgb, p.tint.rgb, p.tint.a), color.a);

    return float4(color.rgb, mask * color.a * p.radiusBlur.z);
}
