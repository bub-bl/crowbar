using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using Crowbar.Engine.Rendering;
using SilkBuffer = Silk.NET.WebGPU.Buffer;
using SilkTextureFormat = Silk.NET.WebGPU.TextureFormat;
using SilkBufferUsage = Silk.NET.WebGPU.BufferUsage;
using SilkShaderStage = Silk.NET.WebGPU.ShaderStage;
using SilkCompareFunction = Silk.NET.WebGPU.CompareFunction;
using SilkVertexFormat = Silk.NET.WebGPU.VertexFormat;
using SilkPrimitiveTopology = Silk.NET.WebGPU.PrimitiveTopology;
using SilkCullMode = Silk.NET.WebGPU.CullMode;
using EngineTextureFormat = Crowbar.Engine.Rendering.TextureFormat;
using EngineBufferUsage = Crowbar.Engine.Rendering.BufferUsage;
using EngineShaderStage = Crowbar.Engine.Rendering.ShaderStage;
using EngineCompareFunction = Crowbar.Engine.Rendering.CompareFunction;
using EngineVertexFormat = Crowbar.Engine.Rendering.VertexFormat;
using EnginePrimitiveTopology = Crowbar.Engine.Rendering.PrimitiveTopology;
using EngineCullMode = Crowbar.Engine.Rendering.CullMode;

namespace Crowbar.Engine;

/// <summary>
/// Internal unsafe boundary for calls whose Silk.NET signatures contain pointers.
/// </summary>
internal static unsafe class WebGpuNative
{
    internal static void ReleaseInstance(WebGPU api, nint handle) =>
        api.InstanceRelease((Instance*)handle);

    internal static void ReleaseAdapter(WebGPU api, nint handle) =>
        api.AdapterRelease((Adapter*)handle);

    internal static void ReleaseDevice(WebGPU api, nint handle) =>
        api.DeviceRelease((Device*)handle);

    internal static void SetPipeline(WebGPU api, WebGpuRenderPassEncoder pass, Silk.NET.WebGPU.RenderPipeline* pipeline) =>
        api.RenderPassEncoderSetPipeline((RenderPassEncoder*)pass.NativeHandle, pipeline);

    internal static void SetViewport(WebGPU api, WebGpuRenderPassEncoder pass, float x, float y, float width, float height) =>
        api.RenderPassEncoderSetViewport((RenderPassEncoder*)pass.NativeHandle, x, y, width, height, 0f, 1f);

    internal static void SetScissorRect(WebGPU api, WebGpuRenderPassEncoder pass, uint x, uint y, uint width, uint height) =>
        api.RenderPassEncoderSetScissorRect((RenderPassEncoder*)pass.NativeHandle, x, y, width, height);

    internal static void SetBindGroup(WebGPU api, WebGpuRenderPassEncoder pass, BindGroup* bindGroup, uint groupIndex) =>
        api.RenderPassEncoderSetBindGroup((RenderPassEncoder*)pass.NativeHandle, groupIndex,
            bindGroup, 0, null);

    internal static void SetVertexBuffer(WebGPU api, WebGpuRenderPassEncoder pass, SilkBuffer* buffer, ulong size) =>
        api.RenderPassEncoderSetVertexBuffer((RenderPassEncoder*)pass.NativeHandle, 0,
            buffer, 0, size);

    internal static void SetIndexBuffer(WebGPU api, WebGpuRenderPassEncoder pass, SilkBuffer* buffer, ulong size) =>
        api.RenderPassEncoderSetIndexBuffer((RenderPassEncoder*)pass.NativeHandle, buffer,
            IndexFormat.Uint32, 0, size);

    internal static void Draw(WebGPU api, WebGpuRenderPassEncoder pass, uint vertexCount) =>
        api.RenderPassEncoderDraw((RenderPassEncoder*)pass.NativeHandle, vertexCount, 1, 0, 0);

    internal static void Draw(WebGPU api, WebGpuRenderPassEncoder pass, uint vertexCount, uint firstVertex) =>
        api.RenderPassEncoderDraw((RenderPassEncoder*)pass.NativeHandle, vertexCount, 1, firstVertex, 0);

    internal static void DrawIndexed(WebGPU api, WebGpuRenderPassEncoder pass, uint indexCount) =>
        api.RenderPassEncoderDrawIndexed((RenderPassEncoder*)pass.NativeHandle, indexCount, 1, 0, 0, 0);

    internal static void DrawInstanced(WebGPU api, WebGpuRenderPassEncoder pass, uint vertexCount, uint instanceCount) =>
        api.RenderPassEncoderDraw((RenderPassEncoder*)pass.NativeHandle, vertexCount, instanceCount, 0, 0);

    internal static void DrawInstanced(WebGPU api, WebGpuRenderPassEncoder pass, uint vertexCount, uint instanceCount, uint firstInstance) =>
        api.RenderPassEncoderDraw((RenderPassEncoder*)pass.NativeHandle, vertexCount, instanceCount, 0, firstInstance);

    internal static WebGpuNativeCommandEncoder CreateCommandEncoder(WebGPU api, WebGpuDevice device) =>
        new((nint)api.DeviceCreateCommandEncoder(device.UnsafeHandle, null));

    internal static WebGpuRenderPassEncoder BeginRenderPass(
        WebGPU api,
        WebGpuNativeCommandEncoder encoder,
        RenderPassDescription description)
    {
        // Depth-only passes (shadow maps) declare no color attachment.
        RenderPassColorAttachment colorAttachment = default;
        RenderPassColorAttachment* colorAttachmentPtr = null;
        if (description.Color is not null)
        {
            // A multisampled attachment cannot be stored directly: it resolves into
            // its companion single-sample texture at pass end (and, per the WebGPU
            // spec, the multisampled attachment itself must then be discarded).
            var resolve = description.Color.ResolveTarget as WebGpuTexture;
            colorAttachment = new RenderPassColorAttachment
            {
                View = ((WebGpuTexture)description.Color.Texture).View,
                LoadOp = ToNative(description.Color.LoadOp),
                StoreOp = resolve is not null
                    ? Silk.NET.WebGPU.StoreOp.Discard
                    : ToNative(description.Color.StoreOp),
                ResolveTarget = resolve?.View,
                ClearValue = new Color
                {
                    R = description.Color.ClearColor.X,
                    G = description.Color.ClearColor.Y,
                    B = description.Color.ClearColor.Z,
                    A = description.Color.ClearColor.W
                }
            };
            colorAttachmentPtr = &colorAttachment;
        }

        RenderPassDepthStencilAttachment depthAttachment = default;
        RenderPassDepthStencilAttachment* depthAttachmentPtr = null;
        if (description.Depth is not null)
        {
            depthAttachment = new RenderPassDepthStencilAttachment
            {
                View = ((WebGpuTexture)description.Depth.Texture).View,
                DepthLoadOp = ToNative(description.Depth.LoadOp),
                DepthStoreOp = ToNative(description.Depth.StoreOp),
                DepthClearValue = description.Depth.ClearValue
            };
            depthAttachmentPtr = &depthAttachment;
        }

        var descriptor = new RenderPassDescriptor
        {
            ColorAttachmentCount = description.Color is null ? 0u : 1u,
            ColorAttachments = colorAttachmentPtr,
            DepthStencilAttachment = depthAttachmentPtr
        };

        return new((nint)api.CommandEncoderBeginRenderPass(
            (CommandEncoder*)encoder.NativeHandle, in descriptor));
    }

    internal static void EndRenderPass(WebGPU api, WebGpuRenderPassEncoder pass) =>
        api.RenderPassEncoderEnd((RenderPassEncoder*)pass.NativeHandle);

    internal static WebGpuNativeCommandBuffer FinishCommandEncoder(WebGPU api, WebGpuNativeCommandEncoder encoder) =>
        new((nint)api.CommandEncoderFinish((CommandEncoder*)encoder.NativeHandle, null));

    internal static void Submit(WebGPU api, WebGpuQueue queue, WebGpuNativeCommandBuffer commandBuffer)
    {
        Silk.NET.WebGPU.CommandBuffer* buffer = (Silk.NET.WebGPU.CommandBuffer*)commandBuffer.NativeHandle;
        api.QueueSubmit((Queue*)queue.NativeHandle, 1, &buffer);
    }

    internal static void ReleaseCommandEncoder(WebGPU api, WebGpuNativeCommandEncoder encoder) =>
        api.CommandEncoderRelease((CommandEncoder*)encoder.NativeHandle);

    internal static void ReleaseCommandBuffer(WebGPU api, WebGpuNativeCommandBuffer commandBuffer) =>
        api.CommandBufferRelease((Silk.NET.WebGPU.CommandBuffer*)commandBuffer.NativeHandle);

    internal static SilkTextureFormat ToNative(EngineTextureFormat format) => format switch
    {
        EngineTextureFormat.Rgba8Unorm => SilkTextureFormat.Rgba8Unorm,
        EngineTextureFormat.Bgra8Unorm => SilkTextureFormat.Bgra8Unorm,
        EngineTextureFormat.Rgba8UnormSrgb => SilkTextureFormat.Rgba8UnormSrgb,
        EngineTextureFormat.Bgra8UnormSrgb => SilkTextureFormat.Bgra8UnormSrgb,
        EngineTextureFormat.Depth24Plus => SilkTextureFormat.Depth24Plus,
        EngineTextureFormat.Depth32Float => SilkTextureFormat.Depth32float,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>
    /// Maps the surface's preferred format to the engine's supported set.
    /// Returns null for formats the engine does not handle yet (e.g.
    /// Rgba16Float); the caller falls back to
    /// <see cref="EngineTextureFormat.Bgra8Unorm"/>.
    /// </summary>
    internal static EngineTextureFormat? ToEngine(SilkTextureFormat format) => format switch
    {
        SilkTextureFormat.Rgba8Unorm => EngineTextureFormat.Rgba8Unorm,
        SilkTextureFormat.Bgra8Unorm => EngineTextureFormat.Bgra8Unorm,
        SilkTextureFormat.Rgba8UnormSrgb => EngineTextureFormat.Rgba8UnormSrgb,
        SilkTextureFormat.Bgra8UnormSrgb => EngineTextureFormat.Bgra8UnormSrgb,
        SilkTextureFormat.Depth24Plus => EngineTextureFormat.Depth24Plus,
        SilkTextureFormat.Depth32float => EngineTextureFormat.Depth32Float,
        _ => null
    };

    internal static SilkBufferUsage ToNative(EngineBufferUsage usage)
    {
        var result = SilkBufferUsage.None;
        if (usage.HasFlag(EngineBufferUsage.Vertex)) result |= SilkBufferUsage.Vertex;
        if (usage.HasFlag(EngineBufferUsage.Index)) result |= SilkBufferUsage.Index;
        if (usage.HasFlag(EngineBufferUsage.Uniform)) result |= SilkBufferUsage.Uniform;
        if (usage.HasFlag(EngineBufferUsage.Storage)) result |= SilkBufferUsage.Storage;
        if (usage.HasFlag(EngineBufferUsage.CopyDst)) result |= SilkBufferUsage.CopyDst;
        return result;
    }

    internal static SilkShaderStage ToNative(EngineShaderStage stage)
    {
        var result = SilkShaderStage.None;
        if (stage.HasFlag(EngineShaderStage.Vertex)) result |= SilkShaderStage.Vertex;
        if (stage.HasFlag(EngineShaderStage.Fragment)) result |= SilkShaderStage.Fragment;
        return result;
    }

    internal static SilkCompareFunction ToNative(EngineCompareFunction function) => function switch
    {
        EngineCompareFunction.Always => SilkCompareFunction.Always,
        EngineCompareFunction.Less => SilkCompareFunction.Less,
        EngineCompareFunction.LessEqual => SilkCompareFunction.LessEqual,
        _ => throw new ArgumentOutOfRangeException(nameof(function))
    };

    internal static SilkVertexFormat ToNative(EngineVertexFormat format) => format switch
    {
        EngineVertexFormat.Float32 => SilkVertexFormat.Float32,
        EngineVertexFormat.Float32x2 => SilkVertexFormat.Float32x2,
        EngineVertexFormat.Float32x3 => SilkVertexFormat.Float32x3,
        EngineVertexFormat.Float32x4 => SilkVertexFormat.Float32x4,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    internal static SilkPrimitiveTopology ToNative(EnginePrimitiveTopology topology) => topology switch
    {
        EnginePrimitiveTopology.TriangleList => SilkPrimitiveTopology.TriangleList,
        EnginePrimitiveTopology.LineList => SilkPrimitiveTopology.LineList,
        EnginePrimitiveTopology.LineStrip => SilkPrimitiveTopology.LineStrip,
        _ => throw new ArgumentOutOfRangeException(nameof(topology))
    };

    internal static SilkCullMode ToNative(EngineCullMode cullMode) => cullMode switch
    {
        EngineCullMode.None => SilkCullMode.None,
        EngineCullMode.Back => SilkCullMode.Back,
        EngineCullMode.Front => SilkCullMode.Front,
        _ => throw new ArgumentOutOfRangeException(nameof(cullMode))
    };

    private static LoadOp ToNative(RenderAttachmentLoadOp op) => op switch
    {
        RenderAttachmentLoadOp.Load => LoadOp.Load,
        RenderAttachmentLoadOp.Clear => LoadOp.Clear,
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    private static StoreOp ToNative(RenderAttachmentStoreOp op) => op switch
    {
        RenderAttachmentStoreOp.Store => StoreOp.Store,
        RenderAttachmentStoreOp.Discard => StoreOp.Discard,
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    /// <summary>
    /// Copies a string into a null-terminated UTF-8 buffer allocated with
    /// Marshal.AllocHGlobal (freed by Marshal.FreeHGlobal). wgpu reads shader
    /// sources as UTF-8, so passing the ANSI conversion would mangle any
    /// non-ASCII byte and make wgpu reject the module.
    /// </summary>
    internal static nint ToUtf8HGlobal(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        nint pointer = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        Marshal.WriteByte(pointer, bytes.Length, 0);
        return pointer;
    }
}
