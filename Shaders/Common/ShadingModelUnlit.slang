#language slang 2026;
// The unlit shading model: forwards the base color with no lighting. It only
// depends on Material, so an unlit pass never pulls in the light/shadow
// bindings it does not use.
module ShadingModelUnlit;

import Material;
import ShadingModel;

public struct ShadingModelUnlit : IShadingModel
{
    static float4 Shade(Material m)
    {
        return float4(m.BaseColor, m.Opacity);
    }
};
