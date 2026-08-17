#language slang 2026;
// The standard PBR shading model: direct lights + shadows + AO + emission,
// then tonemapping, in one s&box-style entry point.
module ShadingModelStandard;

import Scene;
import Material;
import ShadingModel;
import Lighting;
import Color;

public struct ShadingModelStandard : IShadingModel
{
    static float4 Shade(Material m)
    {
        let view_dir = normalize(scene.cameraPosition.xyz - m.WorldPosition);
        var color = Lighting::EvaluateAll(m.WorldPosition, m.Normal, view_dir, m.BaseColor, m.Metallic, m.Roughness);
        color = color * m.Occlusion + m.Emissive;
        return float4(Color::Tonemap(color), m.Opacity);
    }
};
