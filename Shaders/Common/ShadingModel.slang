#language slang 2026;
// The shading-model contract. A resolved Material is handed to a shading
// model's static Shade(), which returns the final fragment color.
module ShadingModel;

import Material;

public interface IShadingModel
{
    static float4 Shade(Material m);
};

// Static-dispatch helper: Shade<ShadingModelStandard>(m) is equivalent to
// ShadingModelStandard::Shade(m), but expresses "shade with this model" at the
// type level so callers can stay agnostic to which model they use.
public float4 Shade<T : IShadingModel>(Material m)
{
    return T.Shade(m);
}
