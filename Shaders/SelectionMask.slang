#include "Common/Transform.slang"

// Selection mask pass: renders the selected entity's mesh as a flat white
// silhouette into the mask texture (Rgba8Unorm, red channel used). Depth is
// tested against the scene depth (LessEqual, no write), so the mask covers
// exactly the visible part of the selection — occlusion clips the outline.
// SelectionOutline.slang then dilates this mask into the real contour.

[[vk::binding(0, 1)]] ConstantBuffer<float4x4> model;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
};

[shader("vertex")]
float4 vs_main(VertexInput input) : SV_Position
{
    let world_position = mul(model, float4(input.position, 1.0));
    return mul(mul(scene.proj, scene.view), world_position);
}

[shader("fragment")]
float4 fs_main() : SV_Target
{
    return float4(1.0, 1.0, 1.0, 1.0);
}
