#language slang 2026;
// Screen-space transforms shared by the UI passes. Each shader declares its
// own viewport uniform (the binding slot varies per pass); only the math is
// shared here.
module Screen;

public struct Screen
{
    // Unit quad [0,1] -> clip space [-1,1] with a flipped Y (top-left origin).
    static float2 UnitToClip(float2 unit)
    {
        return float2(unit.x * 2.0 - 1.0, 1.0 - unit.y * 2.0);
    }

    // Top-left-origin pixel -> clip space, given viewport = (size, 1/size).
    static float2 PixelToClip(float2 pixel, float4 viewport)
    {
        return UnitToClip(pixel * viewport.zw);
    }
};
