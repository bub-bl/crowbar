using System.Runtime.InteropServices;
using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;

namespace Crowbar.Engine;

/// <summary>
/// Owns one WebGPU API instance and its native instance handle.
/// </summary>
public sealed class WebGpuRuntime : IDisposable
{
    internal WebGPU Api { get; }
    internal WebGPU Wgpu => Api;
    public WebGpuInstance Instance { get; }

    public WebGpuRuntime()
    {
        Api = WebGPU.GetApi();
        Instance = new WebGpuInstance(this);
        Log.Info("Created WebGPU runtime.");
    }

    public void ConfigureDebugCallback(WebGpuDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var callback = PfnErrorCallback.From((type, msgPtr, _) =>
        {
            var message = Marshal.PtrToStringUTF8((IntPtr)msgPtr);
            Log.Error($"WGPU Unhandled Error: {type} -> {message}");
        });

        Api.DeviceSetUncapturedErrorCallback(device.UnsafeHandle, callback, null);
    }

    public void Dispose()
    {
        Instance.Dispose();
        Api.Dispose();
        Log.Info("Disposed WebGPU runtime.");
    }

    internal void SetPipeline(WebGpuRenderPassEncoder pass, WebGpuPipeline pipeline) =>
        WebGpuNative.SetPipeline(Api, pass, pipeline.Pipeline);

    internal void SetViewport(WebGpuRenderPassEncoder pass, float x, float y, float width, float height) =>
        WebGpuNative.SetViewport(Api, pass, x, y, width, height);

    internal void SetScissorRect(WebGpuRenderPassEncoder pass, uint x, uint y, uint width, uint height) =>
        WebGpuNative.SetScissorRect(Api, pass, x, y, width, height);

    internal void SetBindGroup(WebGpuRenderPassEncoder pass, WebGpuBindGroup bindGroup, uint groupIndex) =>
        WebGpuNative.SetBindGroup(Api, pass, bindGroup.BindGroup, groupIndex);

    internal void SetVertexBuffer(WebGpuRenderPassEncoder pass, WebGpuBuffer buffer, ulong size) =>
        WebGpuNative.SetVertexBuffer(Api, pass, buffer.Buffer, size);

    internal void SetIndexBuffer(WebGpuRenderPassEncoder pass, WebGpuBuffer buffer, ulong size) =>
        WebGpuNative.SetIndexBuffer(Api, pass, buffer.Buffer, size);

    internal void Draw(WebGpuRenderPassEncoder pass, uint vertexCount) =>
        WebGpuNative.Draw(Api, pass, vertexCount);

    internal void Draw(WebGpuRenderPassEncoder pass, uint vertexCount, uint firstVertex) =>
        WebGpuNative.Draw(Api, pass, vertexCount, firstVertex);

    internal void DrawIndexed(WebGpuRenderPassEncoder pass, uint indexCount) =>
        WebGpuNative.DrawIndexed(Api, pass, indexCount);

    internal void DrawInstanced(WebGpuRenderPassEncoder pass, uint vertexCount, uint instanceCount) =>
        WebGpuNative.DrawInstanced(Api, pass, vertexCount, instanceCount);

    internal void DrawInstanced(WebGpuRenderPassEncoder pass, uint vertexCount, uint instanceCount, uint firstInstance) =>
        WebGpuNative.DrawInstanced(Api, pass, vertexCount, instanceCount, firstInstance);

    internal WebGpuRenderPassEncoder BeginRenderPass(
        WebGpuNativeCommandEncoder encoder,
        RenderPassDescription description) =>
        WebGpuNative.BeginRenderPass(Api, encoder, description);

    internal void EndRenderPass(WebGpuRenderPassEncoder pass) =>
        WebGpuNative.EndRenderPass(Api, pass);

    internal WebGpuNativeCommandBuffer FinishCommandEncoder(WebGpuNativeCommandEncoder encoder) =>
        WebGpuNative.FinishCommandEncoder(Api, encoder);

    internal void Submit(WebGpuQueue queue, WebGpuNativeCommandBuffer commandBuffer) =>
        WebGpuNative.Submit(Api, queue, commandBuffer);

    internal void ReleaseCommandEncoder(WebGpuNativeCommandEncoder encoder) =>
        WebGpuNative.ReleaseCommandEncoder(Api, encoder);

    internal void ReleaseCommandBuffer(WebGpuNativeCommandBuffer commandBuffer) =>
        WebGpuNative.ReleaseCommandBuffer(Api, commandBuffer);

    internal WebGpuNativeCommandEncoder CreateCommandEncoder(WebGpuDevice device) =>
        WebGpuNative.CreateCommandEncoder(Api, device);
}
