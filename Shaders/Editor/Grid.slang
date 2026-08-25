#language slang 2026;
import Color;

// Editor ground grid, drawn as a fullscreen pass after the world's meshes.
// The vertex shader unprojects the near and far planes; the fragment shader
// intersects the view ray with the y = 0 plane, draws the grid lines with
// screen-space anti-aliasing, discards everything between lines, fades into
// the distance, and writes the true depth so meshes can occlude it.

struct GridUniforms
{
    float4x4 view;
    float4x4 proj;
    float4x4 viewInv;
    float4x4 projInv;
    float4 settings;   // x = size (0 = infinite), y = cell size, z = fade distance, w = show axes
    float4 lineColor;
    float4 xAxisColor;
    float4 zAxisColor;
};

[[vk::binding(0, 0)]] ConstantBuffer<GridUniforms> grid;

struct VertexOutput
{
    float4 clip_position : SV_Position;
    [[vk::location(0)]] float3 near_point : TEXCOORD0;
    [[vk::location(1)]] float3 far_point : TEXCOORD1;
};

float3 unprojectPoint(float x, float y, float z)
{
    let p = mul(mul(grid.viewInv, grid.projInv), float4(x, y, z, 1.0));
    return p.xyz / p.w;
}

[shader("vertex")]
VertexOutput vs_main([[vk::location(0)]] float3 position : POSITION)
{
    VertexOutput out;
    out.clip_position = float4(position.xy, 0.0, 1.0);
    out.near_point = unprojectPoint(position.x, position.y, 0.0);
    out.far_point = unprojectPoint(position.x, position.y, 1.0);
    return out;
}

struct FragmentOutput
{
    [[vk::location(0)]] float4 color : SV_Target;
    float depth : SV_Depth;
};

[shader("fragment")]
FragmentOutput fs_main(VertexOutput input)
{
    // Intersect the view ray with the ground plane (y = 0).
    let t = -input.near_point.y / (input.far_point.y - input.near_point.y);
    if (t < 0.0 || t > 1.0)
    {
        discard;
    }

    let fragPos3D = input.near_point + t * (input.far_point - input.near_point);
    if (grid.settings.x > 0.0)
    {
        let halfSize = grid.settings.x * 0.5;
        if (abs(fragPos3D.x) > halfSize || abs(fragPos3D.z) > halfSize)
        {
            discard;
        }
    }
    let clipSpacePos = mul(mul(grid.proj, grid.view), float4(fragPos3D, 1.0));
    let realDepth = clipSpacePos.z / clipSpacePos.w;
    // The grid lies exactly on the ground plane, so its depth can match an
    // object's depth at the contact edge. Nudge it forward by a fraction of
    // a pixel to make the depth comparison deterministic and avoid z-fighting
    // without making the grid ignore objects that are actually in front of it.
    let depthBias = max(fwidth(realDepth) * 0.5, 0.000001);

    let coord = fragPos3D.xz / grid.settings.y;
    let derivative = fwidth(coord);
    let gridLine = abs(frac(coord - 0.5) - 0.5) / derivative;
    let line = min(gridLine.x, gridLine.y);
    // Keep the main axes visible as continuous one-pixel lines. The X axis is
    // the line along X (z = 0) and the Z axis the line along Z (x = 0), so the
    // colors line up with the viewport gizmos (X red, Z blue).
    let minimumz = min(derivative.y, 1.0);
    let minimumx = min(derivative.x, 1.0);
    let onXAxis = abs(fragPos3D.z) <= minimumz;
    let onZAxis = abs(fragPos3D.x) <= minimumx;
    // The grid is transparent between its lines. Discarding those pixels is
    // important because the grid is rendered after the meshes and must not
    // cover objects through an otherwise invisible fragment.
    if (line > 1.0 && !onXAxis && !onZAxis)
    {
        discard;
    }

    var gridColor = grid.lineColor * float4(1.0, 1.0, 1.0, 1.0 - min(line, 1.0));

    if (onXAxis && grid.settings.w > 0.5)
    {
        gridColor = grid.xAxisColor;
    }
    if (onZAxis && grid.settings.w > 0.5)
    {
        gridColor = grid.zAxisColor;
    }

    let fading = max(0.0, 1.0 - length(fragPos3D.xz) / grid.settings.z);

    FragmentOutput out;
    out.color = float4(Color::SrgbToLinear(clamp(gridColor.rgb, float3(0.0), float3(1.0))), gridColor.a) * fading;
    out.depth = max(realDepth - depthBias, 0.0);
    return out;
}
