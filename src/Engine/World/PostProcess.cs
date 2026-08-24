namespace Crowbar.Engine;

/// <summary>Filtering mode of a post-process pass's texture sample.</summary>
public enum PostProcessSampler
{
    Linear,
    Point
}

/// <summary>
/// Base class for fullscreen post-process effects. The renderer runs every
/// enabled <see cref="PostProcess"/> in the world in <see cref="Order"/> between
/// the linear HDR scene and the display texture, calling <see cref="Render"/>
/// with a <see cref="PostProcessContext"/> the effect uses to issue its blits.
///
/// Effects are plain components: derive from this class (or from
/// <see cref="SinglePassPostProcess"/> or <see cref="BasePostProcess{T}"/>),
/// add the fullscreen shader it drives in <c>Shaders/PostProcesses/</c> (or the
/// game project's <c>Content/Shaders/</c>), and it appears in the editor's
/// Add Component list. Attach it to an entity directly for a global effect, or
/// to a <see cref="PostProcessVolume"/> entity to make it spatial.
/// </summary>
public abstract class PostProcess : Component
{
    /// <summary>Execution order in the chain; lower values run first.</summary>
    [Property]
    public int Order { get; set; }

    /// <summary>Filtering mode of this pass's texture sample (linear bilinear, point nearest).</summary>
    [Property]
    public PostProcessSampler Sampler { get; set; } = PostProcessSampler.Linear;

    /// <summary>
    /// Runs the effect: issue one or more <see cref="PostProcessContext.Blit"/>
    /// calls reading <see cref="PostProcessContext.Input"/> and writing
    /// <see cref="PostProcessContext.Output"/> (use
    /// <see cref="PostProcessContext.GetScratchTexture"/> for internal
    /// ping-pong). Called by the renderer each frame while the component is
    /// enabled.
    /// </summary>
    public abstract void Render(PostProcessContext context);
}
