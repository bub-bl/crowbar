using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine;

/// <summary>
/// The renderer state of one post-process pass: the chain texture coming in,
/// the target the effect must write, the scene depth (for effects that need
/// it) and the scratch-texture pool for internal ping-pong. Valid only during
/// <see cref="PostProcess.Render"/>.
/// </summary>
public sealed class PostProcessContext
{
    private readonly Renderer _renderer;
    private readonly ICommandBuffer _commandBuffer;
    private readonly PostProcessSampler _sampler;

    internal PostProcessContext(
        Renderer renderer,
        ICommandBuffer commandBuffer,
        ITexture input,
        ITexture output,
        ITexture depth,
        PostProcessSampler sampler,
        IReadOnlyList<PostProcessEntry> entries,
        double time,
        Camera camera)
    {
        _renderer = renderer;
        _commandBuffer = commandBuffer;
        _sampler = sampler;
        Input = input;
        Output = output;
        Depth = depth;
        Entries = entries;
        Time = (float)time;
        NearPlane = camera.NearPlane;
        FarPlane = camera.FarPlane;
    }

    /// <summary>
    /// Test-only constructor: enough state for <see cref="GetWeighted"/>
    /// blending (via <see cref="Current"/>); <see cref="Blit"/> is unavailable.
    /// </summary>
    internal PostProcessContext(ITexture input, ITexture output, ITexture depth, IReadOnlyList<PostProcessEntry> entries)
        : this(null!, null!, input, output, depth, PostProcessSampler.Linear, entries, 0.0, new Camera())
    {
    }

    /// <summary>The chain texture this pass reads (the scene, or the previous effect's output).</summary>
    public ITexture Input { get; }

    /// <summary>The chain target this effect must write (the display texture for the last pass).</summary>
    public ITexture Output { get; }

    /// <summary>The scene's depth texture, for effects that need it (depth of field, fog, ...).</summary>
    public ITexture Depth { get; }

    /// <summary>
    /// A scratch Rgba16Float texture (0..3, shared per frame) for intermediate
    /// passes inside a multi-pass effect.
    /// </summary>
    public ITexture GetScratchTexture(int index) => _renderer.GetPostProcessScratchTexture(index);

    /// <summary>
    /// The bloom pyramid target at <paramref name="index"/> (½, ¼, ⅛ or 1/16 of
    /// the viewport), for the bright-extract / downsample / blur / combine chain.
    /// </summary>
    public ITexture GetBloomLevelTexture(int index) => _renderer.GetBloomPyramidTexture(index);

    /// <summary>Width of the bloom pyramid level at <paramref name="index"/>.</summary>
    public int GetBloomLevelWidth(int index) => _renderer.GetBloomLevelWidth(index);

    /// <summary>Height of the bloom pyramid level at <paramref name="index"/>.</summary>
    public int GetBloomLevelHeight(int index) => _renderer.GetBloomLevelHeight(index);

    /// <summary>Viewport dimensions in pixels for resolution-aware effects.</summary>
    public Vector2 ViewportSize => new(_renderer.SceneTargetWidth, _renderer.SceneTargetHeight);

    /// <summary>Render time in seconds, wrapped periodically to preserve float precision.</summary>
    public float Time { get; }

    /// <summary>The camera's near clipping plane, for depth-based effects.</summary>
    public float NearPlane { get; }

    /// <summary>The camera's far clipping plane, for depth-based effects.</summary>
    public float FarPlane { get; }

    /// <summary>
    /// The half-resolution depth-of-field ping-pong target at <paramref name="index"/>
    /// (0..2, ½ of the viewport), for the physical-lens scatter/blur/combine chain.
    /// </summary>
    public ITexture GetDepthOfFieldTexture(int index) => _renderer.GetDepthOfFieldTexture(index);

    /// <summary>
    /// Runs one fullscreen pass reading <paramref name="from"/> and writing
    /// <paramref name="to"/> with the shader at <paramref name="shaderPath"/>.
    /// <paramref name="attributes"/> are packed into the shader's group-0
    /// uniform buffer by field name.
    /// </summary>
    public void Blit(ITexture from, ITexture to, string shaderPath, RenderAttributes? attributes = null)
        => Blit(from, to, shaderPath, attributes, null);

    /// <summary>Runs a fullscreen pass that adds its result to the existing target.</summary>
    public void BlitAdditive(ITexture from, ITexture to, string shaderPath, RenderAttributes? attributes = null)
        => BlitAdditive(from, to, shaderPath, attributes, null);

    /// <summary>
    /// Runs one fullscreen pass, optionally binding a second input texture
    /// (bound at slot 3, with its sampler at slot 4) alongside the primary
    /// <paramref name="from"/> (slot 0). Multi-pass effects like Bloom use this
    /// for their final combine, which reads the scene color and the accumulated
    /// glow in one pass.
    /// </summary>
    public void Blit(ITexture from, ITexture to, string shaderPath, RenderAttributes? attributes, ITexture? secondary)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (_renderer is null)
            throw new InvalidOperationException("This post-process context is not bound to a renderer.");
        _renderer.RunPostProcessPass(_commandBuffer, from, to, shaderPath, attributes, _sampler, secondary);
    }

    internal void BlitAdditive(ITexture from, ITexture to, string shaderPath, RenderAttributes? attributes, ITexture? secondary)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (_renderer is null)
            throw new InvalidOperationException("This post-process context is not bound to a renderer.");
        _renderer.RunPostProcessPass(_commandBuffer, from, to, shaderPath, attributes, _sampler, secondary, additive: true);
    }

    /// <summary>The instances of this effect participating in the current frame (for GetWeighted).</summary>
    internal IReadOnlyList<PostProcessEntry> Entries { get; }

    /// <summary>
    /// The context of the effect currently rendering (single render thread),
    /// or null outside a pass. Set by the renderer around each
    /// <see cref="PostProcess.Render"/> call.
    /// </summary>
    internal static PostProcessContext? Current { get; set; }
}

/// <summary>One instance of an effect participating in a frame, with its blend weight.</summary>
internal sealed record PostProcessEntry(PostProcess Instance, float Weight, bool IsGlobal);
