using Silk.NET.WebGPU;
using Crowbar.Engine.Rendering;
using Buffer = Silk.NET.WebGPU.Buffer;

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

    internal static void SetPipeline(WebGPU api, WebGpuRenderPassEncoder pass, WebGpuRenderPipeline pipeline) =>
        api.RenderPassEncoderSetPipeline((RenderPassEncoder*)pass.NativeHandle, (RenderPipeline*)pipeline.NativeHandle);

    internal static unsafe void WriteBuffer<T>(
        WebGPU api,
        WebGpuQueue queue,
        WebGpuBuffer buffer,
        in T data) where T : unmanaged
    {
        T copy = data;
        api.QueueWriteBuffer((Queue*)queue.NativeHandle, (Buffer*)buffer.NativeHandle, 0, &copy,
            (nuint)sizeof(T));
    }

    internal static void SetBindGroup(WebGPU api, WebGpuRenderPassEncoder pass, WebGpuBindGroup bindGroup, uint groupIndex) =>
        api.RenderPassEncoderSetBindGroup((RenderPassEncoder*)pass.NativeHandle, groupIndex,
            (BindGroup*)bindGroup.NativeHandle, 0, null);

    internal static void SetVertexBuffer(WebGPU api, WebGpuRenderPassEncoder pass, WebGpuBuffer buffer, ulong size) =>
        api.RenderPassEncoderSetVertexBuffer((RenderPassEncoder*)pass.NativeHandle, 0,
            (Buffer*)buffer.NativeHandle, 0, size);

    internal static void SetIndexBuffer(
        WebGPU api,
        WebGpuRenderPassEncoder pass,
        WebGpuBuffer buffer,
        WebGpuIndexFormat format,
        ulong size) =>
        api.RenderPassEncoderSetIndexBuffer((RenderPassEncoder*)pass.NativeHandle,
            (Buffer*)buffer.NativeHandle,
            format == WebGpuIndexFormat.Uint16 ? IndexFormat.Uint16 : IndexFormat.Uint32,
            0,
            size);

    internal static void DrawIndexed(WebGPU api, WebGpuRenderPassEncoder pass, uint indexCount) =>
        api.RenderPassEncoderDrawIndexed((RenderPassEncoder*)pass.NativeHandle, indexCount, 1, 0, 0, 0);

    internal static void Draw(WebGPU api, WebGpuRenderPassEncoder pass, uint vertexCount) =>
        api.RenderPassEncoderDraw((RenderPassEncoder*)pass.NativeHandle, vertexCount, 1, 0, 0);

    internal static void DrawInstanced(WebGPU api, WebGpuRenderPassEncoder pass, uint vertexCount, uint instanceCount) =>
        api.RenderPassEncoderDraw((RenderPassEncoder*)pass.NativeHandle, vertexCount, instanceCount, 0, 0);

    internal static WebGpuCommandEncoder CreateCommandEncoder(WebGPU api, WebGpuDevice device) =>
        new((nint)api.DeviceCreateCommandEncoder(device.UnsafeHandle, null));

    internal static WebGpuRenderPassEncoder BeginRenderPass(
        WebGPU api,
        WebGpuCommandEncoder encoder,
        RenderPassDescription description)
    {
        var colorAttachment = new RenderPassColorAttachment
        {
            View = (TextureView*)description.Color.View.NativeHandle,
            LoadOp = ToNative(description.Color.LoadOp),
            StoreOp = ToNative(description.Color.StoreOp),
            ClearValue = new Color
            {
                R = description.Color.ClearColor.X,
                G = description.Color.ClearColor.Y,
                B = description.Color.ClearColor.Z,
                A = description.Color.ClearColor.W
            }
        };

        RenderPassDepthStencilAttachment depthAttachment = default;
        RenderPassDepthStencilAttachment* depthAttachmentPtr = null;
        if (description.Depth is not null)
        {
            depthAttachment = new RenderPassDepthStencilAttachment
            {
                View = (TextureView*)description.Depth.View.NativeHandle,
                DepthLoadOp = ToNative(description.Depth.LoadOp),
                DepthStoreOp = ToNative(description.Depth.StoreOp),
                DepthClearValue = description.Depth.ClearValue
            };
            depthAttachmentPtr = &depthAttachment;
        }

        var descriptor = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
            DepthStencilAttachment = depthAttachmentPtr
        };

        return new((nint)api.CommandEncoderBeginRenderPass(
            (CommandEncoder*)encoder.NativeHandle, in descriptor));
    }

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

    internal static void EndRenderPass(WebGPU api, WebGpuRenderPassEncoder pass) =>
        api.RenderPassEncoderEnd((RenderPassEncoder*)pass.NativeHandle);

    internal static WebGpuCommandBuffer FinishCommandEncoder(WebGPU api, WebGpuCommandEncoder encoder) =>
        new((nint)api.CommandEncoderFinish((CommandEncoder*)encoder.NativeHandle, null));

    internal static void Submit(WebGPU api, WebGpuQueue queue, WebGpuCommandBuffer commandBuffer)
    {
        CommandBuffer* buffer = (CommandBuffer*)commandBuffer.NativeHandle;
        api.QueueSubmit((Queue*)queue.NativeHandle, 1, &buffer);
    }

    internal static void ReleaseCommandEncoder(WebGPU api, WebGpuCommandEncoder encoder) =>
        api.CommandEncoderRelease((CommandEncoder*)encoder.NativeHandle);

    internal static void ReleaseCommandBuffer(WebGPU api, WebGpuCommandBuffer commandBuffer) =>
        api.CommandBufferRelease((CommandBuffer*)commandBuffer.NativeHandle);
}
