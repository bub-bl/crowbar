#include "Common/Transform.wgsl"

// One instanced billboard: world center, color and shape/scale. The quad
// corners are passed per vertex; the fragment shader shapes the sprite
// (circle for lights, diamond for widget tips, ring for the selection).
struct GizmoSpriteParams {
    center: vec4<f32>,        // xyz = world position
    color: vec4<f32>,
    scaleKind: vec4<f32>,     // x = half-size (world units), y = kind (0 circle, 1 diamond, 2 ring)
};

@group(0) @binding(1) var<storage, read> sprites: array<GizmoSpriteParams>;

struct SpriteInput {
    @location(0) corner: vec2<f32>,
};

struct SpriteOutput {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) kind: f32,
};

@vertex
fn vs_main(input: SpriteInput, @builtin(instance_index) instance: u32) -> SpriteOutput {
    let params = sprites[instance];

    // Camera basis from the view matrix: with the engine's storage layout the
    // first row of the view holds (right, up, forward) per component.
    let right = vec3<f32>(scene.view[0].x, scene.view[1].x, scene.view[2].x);
    let up = vec3<f32>(scene.view[0].y, scene.view[1].y, scene.view[2].y);

    var out: SpriteOutput;
    let offset = right * (input.corner.x * params.scaleKind.x) + up * (input.corner.y * params.scaleKind.x);
    let world_position = params.center.xyz + offset;
    out.clip_position = scene.proj * scene.view * vec4<f32>(world_position, 1.0);
    out.uv = input.corner;
    out.color = params.color;
    out.kind = params.scaleKind.y;
    return out;
}

@fragment
fn fs_main(input: SpriteOutput) -> @location(0) vec4<f32> {
    let distance = length(input.uv);
    let diamond = abs(input.uv.x) + abs(input.uv.y);

    var alpha = 0.0;
    if (input.kind < 0.5) {
        alpha = 1.0 - smoothstep(0.42, 0.5, distance);
    } else if (input.kind < 1.5) {
        alpha = 1.0 - smoothstep(0.42, 0.5, diamond);
    } else {
        alpha = (1.0 - smoothstep(0.45, 0.5, distance)) * (1.0 - smoothstep(0.32, 0.37, distance));
    }

    // Discard fully transparent pixels so the blend never darkens the scene
    // behind an invisible sprite.
    if (alpha <= 0.003) {
        discard;
    }
    return vec4<f32>(input.color.rgb, input.color.a * alpha);
}
