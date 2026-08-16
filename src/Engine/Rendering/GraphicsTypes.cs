using System.Numerics;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// Backend-neutral texture format. Only the formats the engine actually uses
/// are exposed; the WebGPU backend maps them to its own enum. The sRGB
/// variants are the surface formats most GPUs prefer (the hardware applies
/// the linear→sRGB encode on store, which is what makes the final image look
/// correct on screen); the UI texture itself stays plain unorm because the UI
/// shader decodes sRGB manually.
/// </summary>
public enum TextureFormat
{
    Rgba8Unorm,
    Bgra8Unorm,
    Rgba8UnormSrgb,
    Bgra8UnormSrgb,
    Depth24Plus,
    Depth32Float
}

/// <summary>Backend-neutral usage flags for GPU buffers.</summary>
[Flags]
public enum BufferUsage
{
    None = 0,
    Vertex = 1 << 0,
    Index = 1 << 1,
    Uniform = 1 << 2,
    Storage = 1 << 3,
    CopyDst = 1 << 4
}

/// <summary>Shader stages a resource binding is visible to.</summary>
[Flags]
public enum ShaderStage
{
    Vertex = 1 << 0,
    Fragment = 1 << 1
}

/// <summary>Backend-neutral depth comparison function.</summary>
public enum CompareFunction
{
    Always,
    Less,
    LessEqual
}

/// <summary>Backend-neutral primitive topology.</summary>
public enum PrimitiveTopology
{
    TriangleList,
    LineList,
    LineStrip
}

/// <summary>Backend-neutral per-vertex attribute format.</summary>
public enum VertexFormat
{
    Float32,
    Float32x2,
    Float32x3,
    Float32x4
}

/// <summary>Kind of a resource bound to a bind-group slot.</summary>
public enum BindingType
{
    UniformBuffer,
    ReadOnlyStorageBuffer,
    Texture,
    /// <summary>Sampled depth texture (shadow maps); compared, not color-filtered.</summary>
    DepthTexture,
    Sampler,
    /// <summary>Depth-comparison sampler used with <see cref="DepthTexture"/>.</summary>
    ComparisonSampler
}

/// <summary>Describes a texture the runtime wants to create.</summary>
public sealed class TextureDescription
{
    public int Width { get; init; }
    public int Height { get; init; }
    public TextureFormat Format { get; init; }

    /// <summary>Usable as a render-target color/depth attachment.</summary>
    public bool RenderTarget { get; init; }

    /// <summary>Usable as a sampled texture in a bind group.</summary>
    public bool Sampled { get; init; }

    /// <summary>Usable as the destination of CPU→GPU pixel uploads.</summary>
    public bool CopyDestination { get; init; }

    /// <summary>Usable as the source of GPU→CPU pixel readbacks.</summary>
    public bool CopySource { get; init; }

    /// <summary>
    /// Multisample count (1 = single-sample). MSAA textures are render targets
    /// only — they cannot be sampled — so rendering resolves into a companion
    /// single-sample texture before sampling.
    /// </summary>
    public int SampleCount { get; init; } = 1;
}

/// <summary>Describes a GPU buffer the runtime wants to create.</summary>
public sealed class BufferDescription
{
    public ulong Size { get; init; }
    public BufferUsage Usage { get; init; }
}

/// <summary>Texture filter mode.</summary>
public enum SamplerFilter
{
    Nearest,
    Linear
}

/// <summary>Texture address (wrap) mode.</summary>
public enum SamplerAddressMode
{
    ClampToEdge,
    Repeat,
    MirrorRepeat
}

/// <summary>
/// Sampler configuration. Defaults reproduce the historic UI sampler
/// (linear filtering, clamp-to-edge, nearest mip selection); material
/// samplers typically switch the address mode to <see cref="SamplerAddressMode.Repeat"/>.
/// </summary>
public sealed class SamplerDescription
{
    public SamplerFilter Filter { get; init; } = SamplerFilter.Linear;
    public SamplerAddressMode AddressMode { get; init; } = SamplerAddressMode.ClampToEdge;
    public SamplerFilter MipmapFilter { get; init; } = SamplerFilter.Nearest;

    /// <summary>
    /// When set, the sampler compares depth samples against a reference value
    /// (used for shadow maps); null produces a plain filtering sampler.
    /// </summary>
    public CompareFunction? Compare { get; init; }
}

/// <summary>One vertex attribute inside a vertex-buffer layout.</summary>
public sealed class VertexAttributeDescription
{
    public VertexFormat Format { get; init; }
    public ulong Offset { get; init; }
    public uint ShaderLocation { get; init; }
}

/// <summary>Layout of a single vertex buffer (one buffer, interleaved attributes).</summary>
public sealed class VertexBufferLayoutDescription
{
    public ulong Stride { get; init; }
    public required VertexAttributeDescription[] Attributes { get; init; }
}

/// <summary>One resource slot of a bind group declared by a pipeline.</summary>
public sealed class BindGroupLayoutBinding
{
    public uint Slot { get; init; }
    public BindingType Type { get; init; }
    public ShaderStage Stages { get; init; }
}

/// <summary>
/// Everything needed to build a render pipeline. Shaders are passed as source
/// (WGSL today); the backend compiles them. The color format must match the
/// swapchain so the pipeline can render onto the surface.
/// </summary>
public sealed class PipelineDescription
{
    public required string ShaderSource { get; init; }
    public required string VertexEntryPoint { get; init; }
    public required string FragmentEntryPoint { get; init; }

    /// <summary>Single interleaved vertex buffer layout (stride + attributes).</summary>
    public required VertexBufferLayoutDescription VertexLayout { get; init; }

    /// <summary>
    /// Bind groups declared by the pipeline, one layout per group. The mesh
    /// scene uses group 0 for per-frame state (scene + lights) and group 1
    /// for per-renderable state (model, material, textures).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<BindGroupLayoutBinding>> BindGroups { get; init; } = [];

    /// <summary>Straight-alpha src-over blending (UI compositor).</summary>
    public bool AlphaBlend { get; init; }

    public bool DepthWriteEnabled { get; init; }
    public CompareFunction DepthCompare { get; init; } = CompareFunction.Always;

    public TextureFormat ColorFormat { get; init; }
    public TextureFormat DepthFormat { get; init; } = TextureFormat.Depth24Plus;

    /// <summary>Primitive topology; defaults to triangles.</summary>
    public PrimitiveTopology Topology { get; init; } = PrimitiveTopology.TriangleList;

    /// <summary>
    /// Multisample count of the render targets this pipeline draws into; must
    /// match the pass attachments (defaults to 1).
    /// </summary>
    public int SampleCount { get; init; } = 1;

    /// <summary>
    /// Depth-only pipeline (shadow map pass): no color target is declared, the
    /// fragment stage outputs no color and only depth is written.
    /// </summary>
    public bool DepthOnly { get; init; }
}

/// <summary>One resource actually bound to a bind-group slot.</summary>
public sealed class BindGroupBinding
{
    public uint Slot { get; init; }
    public IBuffer? Buffer { get; init; }

    /// <summary>Byte range of <see cref="Buffer"/> to bind; defaults to the whole buffer.</summary>
    public ulong BufferSize { get; init; }

    public ITexture? Texture { get; init; }
    public ISampler? Sampler { get; init; }
}

public enum RenderAttachmentLoadOp
{
    Load,
    Clear
}

public enum RenderAttachmentStoreOp
{
    Store,
    Discard
}

/// <summary>
/// Safe engine representation of a color render target. The conversion to the
/// backend's native attachment structure happens only inside the backend.
/// </summary>
public sealed class ColorAttachment
{
    public required ITexture Texture { get; init; }

    /// <summary>
    /// Optional single-sample texture the pass resolves <see cref="Texture"/>
    /// into at its end (required when <see cref="Texture"/> is multisampled,
    /// which cannot be sampled directly). Must share the format and size.
    /// </summary>
    public ITexture? ResolveTarget { get; init; }

    public RenderAttachmentLoadOp LoadOp { get; init; } = RenderAttachmentLoadOp.Clear;
    public RenderAttachmentStoreOp StoreOp { get; init; } = RenderAttachmentStoreOp.Store;
    public Vector4 ClearColor { get; init; } = new(0.12f, 0.12f, 0.14f, 1.0f);
}

/// <summary>Safe engine representation of a depth render target.</summary>
public sealed class DepthAttachment
{
    public required ITexture Texture { get; init; }
    public RenderAttachmentLoadOp LoadOp { get; init; } = RenderAttachmentLoadOp.Clear;
    public RenderAttachmentStoreOp StoreOp { get; init; } = RenderAttachmentStoreOp.Store;
    public float ClearValue { get; init; } = 1.0f;
}

/// <summary>
/// Safe render-pass description. Only backend-neutral types are exposed; the
/// conversion to the backend's native structures happens inside the backend.
/// </summary>
public sealed class RenderPassDescription
{
    /// <summary>Null for depth-only passes (shadow maps).</summary>
    public ColorAttachment? Color { get; init; }
    public DepthAttachment? Depth { get; init; }
}
