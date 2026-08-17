#language slang 2026;
// Per-frame scene state bound to group 0 binding 0: the camera and the clock.
// Every 3D pass imports this module to see the camera.
module Scene;

public struct SceneUniforms
{
    float4x4 view;
    float4x4 proj;
    float4 cameraPosition;
    float4 time;            // x = elapsed seconds
};

[[vk::binding(0, 0)]] public ConstantBuffer<SceneUniforms> scene;
