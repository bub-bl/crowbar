#language slang 2026;
module Cubemap;

public struct Cubemap
{
    static float3 Direction(uint face, float2 uv)
    {
        let p = uv * 2.0 - 1.0;
        switch (face)
        {
            case 0: return normalize(float3(1.0, -p.y, -p.x));
            case 1: return normalize(float3(-1.0, -p.y, p.x));
            case 2: return normalize(float3(p.x, 1.0, p.y));
            case 3: return normalize(float3(p.x, -1.0, -p.y));
            case 4: return normalize(float3(p.x, -p.y, 1.0));
            default: return normalize(float3(-p.x, -p.y, -1.0));
        }
    }
};
