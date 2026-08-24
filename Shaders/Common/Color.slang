#language slang 2026;
// Color-space and display transforms shared by the 3D pipeline and the UI
// compositor. These were previously copy-pasted across the shaders; they live
// here so the sRGB decode and tonemapping can never drift apart.
module Color;

public struct Color
{
    // Rec. 709 sRGB -> linear (piecewise), for decoding raster/UI colors.
    static float3 SrgbToLinear(float3 c)
    {
        let lo = c / 12.92;
        let hi = pow((c + 0.055) / 1.055, float3(2.4));
        return select(c <= float3(0.04045), lo, hi);
    }

    // Rec. 709 linear -> sRGB (piecewise), the display encode written when a
    // linear scene is presented to a plain 8-bit (non-sRGB) surface.
    static float3 LinearToSrgb(float3 c)
    {
        let lo = c * 12.92;
        let hi = 1.055 * pow(max(c, float3(0.0)), float3(1.0 / 2.4)) - 0.055;
        return select(c <= float3(0.0031308), lo, hi);
    }

    // Reinhard tonemap + gamma encode, the display transform of the 3D path.
    static float3 Tonemap(float3 c)
    {
        let mapped = c / (c + float3(1.0));
        return pow(mapped, float3(1.0 / 2.2));
    }

    // Rec. 709 relative luminance.
    static float Luminance(float3 c)
    {
        return dot(c, float3(0.2126, 0.7152, 0.0722));
    }
};
