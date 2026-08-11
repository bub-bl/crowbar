#include "Common/Transform.wgsl"

// One instanced widget element: a thick shaft (screen-space quad between the
// origin and the tip) or an arrowhead (triangle at the tip). All sizes are in
// screen pixels and the expansion happens in NDC space, so the widget keeps a
// constant on-screen size at any distance and any viewport aspect.
struct GizmoWidgetElement {
    start: vec4<f32>,        // xyz = world start (widget origin)
    end: vec4<f32>,          // xyz = world end (shaft tip / head apex)
    color: vec4<f32>,
    sizes: vec4<f32>,        // x = kind (0 shaft, 1 head), y = shaft half-width (px),
                             // z = head length (px), w = head half-width (px)
    viewport: vec4<f32>,     // x = width, y = height in pixels (vec4 keeps the
                             // element stride a multiple of 16)
};

@group(0) @binding(1) var<storage, read> elements: array<GizmoWidgetElement>;

struct LineInput {
    @location(0) uv: vec2<f32>,
    // Shaft: x = 0 at the origin, 1 at the tip; y = -1/+1 across.
    // Head:  (0, 0) = apex, (1, -1) / (1, 1) = base corners.
};

struct LineOutput {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) color: vec4<f32>,
};

@vertex
fn vs_main(input: LineInput, @builtin(instance_index) instance: u32) -> LineOutput {
    let element = elements[instance];

    let clipStart = scene.proj * scene.view * element.start;
    let clipEnd = scene.proj * scene.view * element.end;
    let ndcStart = clipStart.xy / clipStart.w;
    let ndcEnd = clipEnd.xy / clipEnd.w;

    // Perpendicular direction, computed in pixels so the thickness is exact
    // regardless of the viewport aspect ratio.
    let pixelsPerNdc = vec2<f32>(0.5 * element.viewport.x, 0.5 * element.viewport.y);
    let dirPx = (ndcEnd - ndcStart) * pixelsPerNdc;
    // Normalize before converting back to NDC. Without this normalization the
    // perpendicular carried the full axis length, turning a 3px half-width
    // into a giant wedge hundreds of pixels wide.
    let perpPx = normalize(vec2<f32>(-dirPx.y, dirPx.x));
    let perpNdc = perpPx / pixelsPerNdc;

    var ndcPos = ndcStart;
    var depth = clipStart.z;
    var w = clipStart.w;

    if (element.sizes.x < 0.5) {
        // Shaft: quad from the origin to the tip, expanded perpendicular by
        // the half-width in pixels.
        ndcPos = mix(ndcStart, ndcEnd, input.uv.x) + perpNdc * (input.uv.y * element.sizes.y);
        depth = mix(clipStart.z, clipEnd.z, input.uv.x);
        w = mix(clipStart.w, clipEnd.w, input.uv.x);
    } else {
        // Arrowhead: apex at the tip, base pulled back along the projected
        // axis, corners expanded perpendicular by the head half-width.
        let axisNdc = normalize(ndcEnd - ndcStart);
        let base = ndcEnd - axisNdc * (2.0 * element.sizes.z / element.viewport.y);
        if (input.uv.x < 0.5) {
            ndcPos = ndcEnd;
        } else {
            ndcPos = base + perpNdc * (input.uv.y * element.sizes.w);
        }
        depth = clipEnd.z;
        w = clipEnd.w;
    }

    var out: LineOutput;
    out.clip_position = vec4<f32>(ndcPos * w, depth, w);
    out.color = element.color;
    return out;
}

@fragment
fn fs_main(input: LineOutput) -> @location(0) vec4<f32> {
    return input.color;
}
