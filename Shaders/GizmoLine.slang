#include "Common/Transform.slang"

// One widget element. Shafts use pixel sizes; cones and rings use world sizes
// computed for the element depth by GizmoRenderer.
struct GizmoWidgetElement
{
    float4 start;        // xyz = world start (widget origin)
    float4 end;          // xyz = world end (shaft tip / cone apex)
    float4 color;
    float4 sizes;        // shaft: x = kind (0), y = half-width px;
                         // cone:  x = kind (1), z = length world, w = radius world
                         // ring:  x = kind (2), z = ring radius world, w = tube radius world
    float4 viewport;     // x = width, y = height in pixels
};

[[vk::binding(1, 0)]] StructuredBuffer<GizmoWidgetElement> elements;

struct LineInput
{
    // Shaft vertices: xy = (along, across), z/w unused.
    // Cone vertices: xy = unit-circle position, z = 0 on the base / 1 at apex.
    // Ring vertices: xy = ring circle (cosθ, sinθ), zw = tube cross-section (cosφ, sinφ).
    [[vk::location(0)]] float4 shape : POSITION;
};

struct LineOutput
{
    float4 clip_position : SV_Position;
    [[vk::location(0)]] float4 color : TEXCOORD0;
};

[shader("vertex")]
LineOutput vs_main(LineInput input, uint instance : SV_InstanceID)
{
    let element = elements[instance];
    LineOutput out;

    if (element.sizes.x < 0.5)
    {
        // The shaft is a screen-space quad. Normalize the perpendicular before
        // converting it to NDC; otherwise the axis length becomes the apparent
        // line thickness.
        let clipStart = mul(mul(scene.proj, scene.view), element.start);
        let clipEnd = mul(mul(scene.proj, scene.view), element.end);
        let ndcStart = clipStart.xy / clipStart.w;
        let ndcEnd = clipEnd.xy / clipEnd.w;
        let pixelsPerNdc = float2(0.5 * element.viewport.x, 0.5 * element.viewport.y);
        let dirPx = (ndcEnd - ndcStart) * pixelsPerNdc;
        let perpPx = normalize(float2(-dirPx.y, dirPx.x));
        let perpNdc = perpPx / pixelsPerNdc;
        let ndcPos = lerp(ndcStart, ndcEnd, input.shape.x)
                   + perpNdc * (input.shape.y * element.sizes.y);
        let depth = lerp(clipStart.z, clipEnd.z, input.shape.x);
        let w = lerp(clipStart.w, clipEnd.w, input.shape.x);
        out.clip_position = float4(ndcPos * w, depth, w);
    }
    else if (element.sizes.x < 1.5)
    {
        // The arrowhead is a real cone aligned with the world-space gizmo axis.
        // Its base is at `end - axis * length`; the radial basis makes the cone
        // visible from every camera angle instead of looking like a billboard.
        // The reference vector must not be written as `var + conditional
        // assignment`: slangc hoists the constant initializer into a
        // function-scope `const`, which wgpu-native's WGSL parser rejects. A
        // ternary emits plain assignments instead.
        let axis = normalize(element.end.xyz - element.start.xyz);
        let reference = abs(axis.y) > 0.99 ? float3(1.0, 0.0, 0.0) : float3(0.0, 1.0, 0.0);
        let side = normalize(cross(axis, reference));
        let up = normalize(cross(axis, side));
        let base = element.end.xyz - axis * element.sizes.z;
        let worldPosition = base
                           + side * (input.shape.x * element.sizes.w)
                           + up * (input.shape.y * element.sizes.w)
                           + axis * (input.shape.z * element.sizes.z);
        out.clip_position = mul(mul(scene.proj, scene.view), float4(worldPosition, 1.0));
    }
    else
    {
        // The rotation ring is a torus: a screen-constant tube swept around
        // the ring circle in the plane perpendicular to the axis. `end` only
        // carries the axis direction; the ring radius and tube radius come from
        // `sizes` (z and w). shape.xy = ring circle (cosθ, sinθ); shape.zw = tube
        // cross-section (cosφ, sinφ), so the tube keeps its thickness even when
        // the ring is viewed edge-on.
        // The reference vector must not be written as `var + conditional
        // assignment`: slangc hoists the constant initializer into a
        // function-scope `const`, which wgpu-native's WGSL parser rejects. A
        // ternary emits plain assignments instead.
        let axis = normalize(element.end.xyz - element.start.xyz);
        let reference = abs(axis.y) > 0.99 ? float3(1.0, 0.0, 0.0) : float3(0.0, 1.0, 0.0);
        let side = normalize(cross(axis, reference));
        let up = normalize(cross(axis, side));
        let ringRadial = side * input.shape.x + up * input.shape.y;
        let radius = element.sizes.z + input.shape.z * element.sizes.w;
        let worldPosition = element.start.xyz
                           + ringRadial * radius
                           + axis * (input.shape.w * element.sizes.w);
        out.clip_position = mul(mul(scene.proj, scene.view), float4(worldPosition, 1.0));
    }

    out.color = element.color;
    return out;
}

[shader("fragment")]
float4 fs_main(LineOutput input) : SV_Target
{
    return input.color;
}
