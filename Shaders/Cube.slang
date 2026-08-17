struct Camera
{
    float4x4 view;
    float4x4 projection;
};

[[vk::binding(0, 0)]] ConstantBuffer<Camera> camera;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 color : COLOR;
};

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 color : TEXCOORD0;
};

[shader("vertex")]
VertexOutput vs_main(VertexInput input)
{
    VertexOutput output;
    let worldPosition = float4(input.position, 1.0);
    output.position = mul(mul(camera.projection, camera.view), worldPosition);
    output.color = input.color;
    return output;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    return float4(input.color, 1.0);
}
