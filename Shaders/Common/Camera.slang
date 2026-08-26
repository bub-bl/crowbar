#language slang 2026;
// Per-frame camera state bound to group 0 binding 0: view, projection,
// position and the clock. Every 3D pass imports this module to see the
// camera.
module Camera;

public struct CameraUniforms
{
    float4x4 view;
    float4x4 proj;
    float4x4 previousViewProj;
    float4 jitter;          // xy = this frame's projection jitter (UV), zw = previous frame's
    float4 cameraPosition;
    float4 time;            // x = elapsed seconds
};

[[vk::binding(0, 0)]] public ConstantBuffer<CameraUniforms> camera;
