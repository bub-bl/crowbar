// Shared per-frame scene state. The engine binds these to group 0 binding 0;
// every shader that includes this file sees the camera and the clock.

struct SceneUniforms {
    view: mat4x4<f32>,
    proj: mat4x4<f32>,
    cameraPosition: vec4<f32>,
    time: vec4<f32>,            // x = elapsed seconds
};

@group(0) @binding(0) var<uniform> scene: SceneUniforms;
