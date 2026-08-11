#include "Common/Transform.wgsl"

// Selection mask pass: renders the selected entity's mesh as a flat white
// silhouette into the mask texture (Rgba8Unorm, red channel used). Depth is
// tested against the scene depth (LessEqual, no write), so the mask covers
// exactly the visible part of the selection — occlusion clips the outline.
// SelectionOutline.wgsl then dilates this mask into the real contour.

@group(1) @binding(0) var<uniform> model: mat4x4<f32>;

struct VertexInput {
    @location(0) position: vec3<f32>,
};

@vertex
fn vs_main(input: VertexInput) -> @builtin(position) vec4<f32> {
    let world_position = model * vec4<f32>(input.position, 1.0);
    return scene.proj * scene.view * world_position;
}

@fragment
fn fs_main() -> @location(0) vec4<f32> {
    return vec4<f32>(1.0, 1.0, 1.0, 1.0);
}
