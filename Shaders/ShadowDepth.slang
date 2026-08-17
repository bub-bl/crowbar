// Depth-only shadow pass. Renders a mesh's position through the current
// light-face view-projection matrix into the shadow atlas. There is no color
// output: the rasterizer writes the interpolated clip depth automatically, so
// the fragment stage is an empty entry point.

[[vk::binding(0, 0)]] ConstantBuffer<float4x4> lightViewProj;
[[vk::binding(0, 1)]] ConstantBuffer<float4x4> model;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
};

[shader("vertex")]
float4 vs_main(VertexInput input) : SV_Position
{
    let worldPosition = mul(model, float4(input.position, 1.0));
    return mul(lightViewProj, worldPosition);
}

[shader("fragment")]
void fs_main()
{
}
