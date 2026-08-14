#include "Common/Transform.wgsl"

// One widget element. Shafts use pixel sizes; cones and rings use world sizes
// computed for the element depth by GizmoRenderer.
struct GizmoWidgetElement {
    start: vec4<f32>,        // xyz = world start (widget origin)
    end: vec4<f32>,          // xyz = world end (shaft tip / cone apex)
    color: vec4<f32>,
    sizes: vec4<f32>,        // shaft: x = kind (0), y = half-width px;
                             // cone:  x = kind (1), z = length world, w = radius world
                             // ring:  x = kind (2), z = radius world, w = half-thickness world
    viewport: vec4<f32>,     // x = width, y = height in pixels
};

@group(0) @binding(1) var<storage, read> elements: array<GizmoWidgetElement>;

struct LineInput {
    // Shaft vertices: xy = (along, across), z/w unused.
    // Cone vertices: xy = unit-circle position, z = 0 on the base / 1 at apex.
    // Ring vertices: xy = unit-circle position, z = inner(-1) / outer(+1).
    @location(0) shape: vec4<f32>,
};

struct LineOutput {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) color: vec4<f32>,
};

@vertex
fn vs_main(input: LineInput, @builtin(instance_index) instance: u32) -> LineOutput {
    let element = elements[instance];
    var out: LineOutput;

    if (element.sizes.x < 0.5) {
        // The shaft is a screen-space quad. Normalize the perpendicular before
        // converting it to NDC; otherwise the axis length becomes the apparent
        // line thickness.
        let clipStart = scene.proj * scene.view * element.start;
        let clipEnd = scene.proj * scene.view * element.end;
        let ndcStart = clipStart.xy / clipStart.w;
        let ndcEnd = clipEnd.xy / clipEnd.w;
        let pixelsPerNdc = vec2<f32>(0.5 * element.viewport.x, 0.5 * element.viewport.y);
        let dirPx = (ndcEnd - ndcStart) * pixelsPerNdc;
        let perpPx = normalize(vec2<f32>(-dirPx.y, dirPx.x));
        let perpNdc = perpPx / pixelsPerNdc;
        let ndcPos = mix(ndcStart, ndcEnd, input.shape.x)
                   + perpNdc * (input.shape.y * element.sizes.y);
        let depth = mix(clipStart.z, clipEnd.z, input.shape.x);
        let w = mix(clipStart.w, clipEnd.w, input.shape.x);
        out.clip_position = vec4<f32>(ndcPos * w, depth, w);
    } else if (element.sizes.x < 1.5) {
        // The arrowhead is a real cone aligned with the world-space gizmo axis.
        // Its base is at `end - axis * length`; the radial basis makes the cone
        // visible from every camera angle instead of looking like a billboard.
        let axis = normalize(element.end.xyz - element.start.xyz);
        var reference = vec3<f32>(0.0, 1.0, 0.0);
        if (abs(axis.y) > 0.99) {
            reference = vec3<f32>(1.0, 0.0, 0.0);
        }
        let side = normalize(cross(axis, reference));
        let up = normalize(cross(axis, side));
        let base = element.end.xyz - axis * element.sizes.z;
        let worldPosition = base
                           + side * (input.shape.x * element.sizes.w)
                           + up * (input.shape.y * element.sizes.w)
                           + axis * (input.shape.z * element.sizes.z);
        out.clip_position = scene.proj * scene.view * vec4<f32>(worldPosition, 1.0);
    } else {
        // The rotation ring is a world-space annulus in the plane perpendicular
        // to the element axis. `end` only carries the axis direction; the radius
        // and half-thickness come from `sizes` (z and w).
        let axis = normalize(element.end.xyz - element.start.xyz);
        var reference = vec3<f32>(0.0, 1.0, 0.0);
        if (abs(axis.y) > 0.99) {
            reference = vec3<f32>(1.0, 0.0, 0.0);
        }
        let side = normalize(cross(axis, reference));
        let up = normalize(cross(axis, side));
        let radius = element.sizes.z + input.shape.z * element.sizes.w;
        let worldPosition = element.start.xyz
                           + side * (input.shape.x * radius)
                           + up * (input.shape.y * radius);
        out.clip_position = scene.proj * scene.view * vec4<f32>(worldPosition, 1.0);
    }

    out.color = element.color;
    return out;
}

@fragment
fn fs_main(input: LineOutput) -> @location(0) vec4<f32> {
    return input.color;
}
