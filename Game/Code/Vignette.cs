// Demo game project: a post-process effect shipped entirely from game code —
// this component plus a fullscreen shader in Content/Shaders/. The engine's
// post-process API is public: derive from BasePostProcess<T> (volume-blended)
// or SinglePassPostProcess (declarative), attach the component to any entity
// from the editor, and the shader is compiled and hot-reloaded by the
// editor's shader watcher (edit Vignette.slang while the editor runs).

using Crowbar.Engine;

namespace Game;

/// <summary>
/// Darkens the frame corners. Attach it to any entity from the inspector
/// (Add component); the settings blend across PostProcessVolume instances
/// through <see cref="BasePostProcess{T}.GetWeighted{T}"/>.
/// </summary>
public sealed class Vignette : BasePostProcess<Vignette>
{
    /// <summary>Corner darkening strength (0 = none).</summary>
    [Property]
    public float Intensity { get; set; } = 0.4f;

    /// <summary>Normalized distance from the center where the fade starts (0..1).</summary>
    [Property]
    public float Radius { get; set; } = 0.65f;

    public override void Render(PostProcessContext context)
    {
        var intensity = GetWeighted(effect => effect.Intensity);
        var radius = GetWeighted(effect => effect.Radius);

        context.Blit(context.Input, context.Output, "Content/Shaders/Vignette.wgsl",
            new RenderAttributes()
                .Set("intensity", intensity)
                .Set("radius", radius));
    }
}
