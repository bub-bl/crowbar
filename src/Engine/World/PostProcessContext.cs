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
        IReadOnlyList<PostProcessEntry> entries)
    {
        _renderer = renderer;
        _commandBuffer = commandBuffer;
        _sampler = sampler;
        Input = input;
        Output = output;
        Depth = depth;
        Entries = entries;
    }

    /// <summary>
    /// Test-only constructor: enough state for <see cref="GetWeighted"/>
    /// blending (via <see cref="Current"/>); <see cref="Blit"/> is unavailable.
    /// </summary>
    internal PostProcessContext(ITexture input, ITexture output, ITexture depth, IReadOnlyList<PostProcessEntry> entries)
        : this(null!, null!, input, output, depth, PostProcessSampler.Linear, entries)
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
    /// Runs one fullscreen pass reading <paramref name="from"/> and writing
    /// <paramref name="to"/> with the shader at <paramref name="shaderPath"/>.
    /// <paramref name="attributes"/> are packed into the shader's group-0
    /// uniform buffer by field name.
    /// </summary>
    public void Blit(ITexture from, ITexture to, string shaderPath, RenderAttributes? attributes = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (_renderer is null)
            throw new InvalidOperationException("This post-process context is not bound to a renderer.");
        _renderer.RunPostProcessPass(_commandBuffer, from, to, shaderPath, attributes, _sampler);
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
