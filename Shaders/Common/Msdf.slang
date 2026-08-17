#language slang 2026;
// Multi-channel signed-distance-field helpers shared by the glyph and
// glyph-shadow passes.
module Msdf;

public struct Msdf
{
    static float Median(float r, float g, float b)
    {
        return max(min(r, g), min(max(r, g), b));
    }

    // Reference MSDF antialiasing from msdfgen. Derive the screen-space range
    // from the UV gradient, rather than from the reconstructed distance: the
    // median has discontinuities at edge-color transitions, so fwidth(distance)
    // can turn a valid contour into a broad horizontal smear.
    static float ScreenPixelRange(Texture2D atlas, float2 uv, float spread)
    {
        uint width, height;
        atlas.GetDimensions(width, height);
        let unitRange = float2(spread) / float2(width, height);
        let dx = ddx(uv);
        let dy = ddy(uv);
        let screenTexSize = rsqrt(dx * dx + dy * dy);
        return max(0.5 * dot(unitRange, screenTexSize), 1.0);
    }
};
