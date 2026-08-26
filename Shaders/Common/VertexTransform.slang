#language slang 2026;
// The single place that maps a model-space mesh vertex through the per-object
// model matrix and the per-frame camera into clip space, carrying the
// world-space normal/tangent frame and UVs to the fragment stage. Every
// shader using the standard mesh vertex layout (Standard, StandardPbr, Unlit)
// reuses this instead of hand-writing the transform.
module VertexTransform;

import Camera;
import Vertex;

public struct VertexTransform
{
    static VertexOutput ToClip(VertexInput input, float4x4 model, float4x4 previousModel)
    {
        VertexOutput output;
        let world_position = mul(model, float4(input.position, 1.0));
        output.world_position = world_position.xyz;

        let normal_matrix = (float3x3)model;
        output.normal = normalize(mul(normal_matrix, input.normal));
        output.tangent = float4(normalize(mul(normal_matrix, input.tangent.xyz)), input.tangent.w);
        output.uv = input.uv;
        output.clip_position = mul(mul(camera.proj, camera.view), world_position);

        // Project the same vertex through the previous frame's view-projection
        // and the object's previous transform, so a fragment can subtract the
        // two NDC positions to get its screen-space motion vector. previousViewProj
        // already embeds last frame's projection, so compose it directly.
        let previous_world_position = mul(previousModel, float4(input.position, 1.0));
        output.previous_clip_position = mul(camera.previousViewProj, previous_world_position);
        return output;
    }
};
