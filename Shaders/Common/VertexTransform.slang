// Shared standard-mesh vertex pipeline: the single place that maps a
// model-space mesh vertex through the per-object model matrix and the
// per-frame camera into clip space, carrying the world-space normal/tangent
// frame and UVs to the fragment stage. Every shader using the standard mesh
// vertex layout (Mesh, Pbr, Unlit) reuses this instead of hand-writing the
// transform.

#include "Common/Transform.slang"
#include "Common/VertexIO.slang"

VertexOutput transformVertex(VertexInput input, float4x4 model)
{
    VertexOutput output;
    let world_position = mul(model, float4(input.position, 1.0));
    output.world_position = world_position.xyz;

    let normal_matrix = (float3x3)model;
    output.normal = normalize(mul(normal_matrix, input.normal));
    output.tangent = float4(normalize(mul(normal_matrix, input.tangent.xyz)), input.tangent.w);
    output.uv = input.uv;
    output.clip_position = mul(mul(scene.proj, scene.view), world_position);
    return output;
}
