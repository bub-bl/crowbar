#language slang 2026;
// The standard PBR shading model: direct lights + shadows + AO + emission,
// then tonemapping, in one s&box-style entry point.
module ShadingModelStandard;

import Camera;
import Material;
import ShadingModel;
import Lighting;
import Color;
import Environment;

public struct ShadingModelStandard : IShadingModel
{
    static float4 Shade(Material m)
    {
        let view_dir = normalize(camera.cameraPosition.xyz - m.WorldPosition);
        var color = Lighting::EvaluateAll(m.WorldPosition, m.Normal, view_dir, m.BaseColor, m.Metallic, m.Roughness);
        color = color * m.Occlusion
            + EnvironmentLighting::EvaluateIbl(
                m.Normal, view_dir, m.BaseColor, m.Metallic, m.Roughness, m.Occlusion)
            + m.Emissive;
        return float4(Color::Tonemap(color), m.Opacity);
    }
};
