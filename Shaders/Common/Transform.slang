// Shared per-frame scene state. The engine binds these to group 0 binding 0;
// every shader that includes this file sees the camera and the clock.

struct SceneUniforms
{
    float4x4 view;
    float4x4 proj;
    float4 cameraPosition;
    float4 time;            // x = elapsed seconds
};

[[vk::binding(0, 0)]] ConstantBuffer<SceneUniforms> scene;
