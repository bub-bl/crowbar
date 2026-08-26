#language slang 2026;
// Per-pixel screen-space motion vector helpers shared by every lit/unlit mesh
// shader. The fragment stage subtracts the previous frame's NDC position from
// the current one to recover how the surface moved between frames, which TAA
// and motion blur consume from the velocity render target.
module Velocity;

import Camera;
import Vertex;

public struct FragmentOutput
{
    [[vk::location(0)]] float4 color : SV_Target;
    [[vk::location(1)]] float2 velocity : SV_Target1;
};

public struct Velocity
{
    // Screen-space UV is top-left origin (WebGPU framebuffer), so NDC +y maps
    // to v = 0 (mirroring how the fullscreen post-process triangles build their
    // input.uv). The projection is jittered by a sub-pixel offset each frame
    // (camera.jitter, in UV); subtract it from both positions so the returned
    // velocity is the surface's *real* screen motion, not the sample offset —
    // the TAA pass then reprojects cleanly with `uv - velocity`.
    static float2 Compute(VertexOutput input)
    {
        let jitter_cur = camera.jitter.xy;
        let jitter_prev = camera.jitter.zw;
        let current_uv = float2(
            input.clip_position.x / input.clip_position.w * 0.5 + 0.5,
            0.5 - input.clip_position.y / input.clip_position.w * 0.5) - jitter_cur;
        let previous_uv = float2(
            input.previous_clip_position.x / input.previous_clip_position.w * 0.5 + 0.5,
            0.5 - input.previous_clip_position.y / input.previous_clip_position.w * 0.5) - jitter_prev;
        return current_uv - previous_uv;
    }
}