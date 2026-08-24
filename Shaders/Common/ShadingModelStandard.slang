#language slang 2026;
// The standard PBR shading model: direct lights + shadows + AO + emission.
// Outputs linear HDR radiance; the display transform (tonemapping) runs once
// in the post-process pass, for the whole frame.
module ShadingModelStandard;

import Camera;
import Material;
import ShadingModel;
import Lighting;
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
        return float4(color, m.Opacity);
    }
};
