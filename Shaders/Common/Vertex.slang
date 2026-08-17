#language slang 2026;
// The engine's standard mesh vertex layout: position (3) + normal (3) +
// tangent (4) + uv (2) floats, 48 bytes per vertex. Every lit/unlit mesh
// shader imports this module instead of re-declaring the vertex structs.
module Vertex;

public struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
    [[vk::location(2)]] float4 tangent : TANGENT;
    [[vk::location(3)]] float2 uv : TEXCOORD0;
};

public struct VertexOutput
{
    float4 clip_position : SV_Position;
    [[vk::location(0)]] float3 world_position : TEXCOORD0;
    [[vk::location(1)]] float3 normal : TEXCOORD1;
    [[vk::location(2)]] float4 tangent : TEXCOORD2;
    [[vk::location(3)]] float2 uv : TEXCOORD3;
};
