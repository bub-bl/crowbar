// Shared vertex input/output for the engine's standard mesh layout:
// position (3) + normal (3) + tangent (4) + uv (2) floats, 48 bytes per
// vertex. Every lit mesh shader includes this file instead of re-declaring
// the vertex structs.

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) tangent: vec4<f32>,
    @location(3) uv: vec2<f32>,
};

struct VertexOutput {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) world_position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) tangent: vec4<f32>,
    @location(3) uv: vec2<f32>,
};
