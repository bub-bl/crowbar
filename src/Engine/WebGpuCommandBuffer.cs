using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;

namespace Crowbar.Engine;

/// <summary>
/// WebGPU-backed <see cref="ICommandBuffer"/>: owns the native command encoder
/// and records render passes until <see cref="Submit"/> finishes and submits
/// the command buffer to the queue.
/// </summary>
public sealed unsafe class WebGpuCommandBuffer : ICommandBuffer
{
    private readonly WebGpuRuntime _runtime;
    private readonly WebGpuQueue _queue;
    private WebGpuNativeCommandEncoder _encoder;
    private WebGpuRenderPass? _activePass;
    private bool _submitted;
    private bool _disposed;

    internal WebGpuCommandBuffer(WebGpuRuntime runtime, WebGpuDevice device, WebGpuQueue queue)
    {
        _runtime = runtime;
        _queue = queue;
        _encoder = runtime.CreateCommandEncoder(device);
    }

    public IRenderPass BeginRenderPass(RenderPassDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (_disposed) throw new ObjectDisposedException(nameof(WebGpuCommandBuffer));
        if (_submitted) throw new InvalidOperationException("The command buffer has already been submitted.");
        if (_activePass != null) throw new InvalidOperationException("A render pass is already active.");
        if (description.Color is null && description.Depth is null)
            throw new ArgumentException("A color or depth attachment is required.", nameof(description));
        if (description.Color is not null && description.Color.Texture is null)
            throw new ArgumentException("A valid color attachment is required.", nameof(description));

        var handle = _runtime.BeginRenderPass(_encoder, description);
        _activePass = new WebGpuRenderPass(this, _runtime, handle);
        return _activePass;
    }

    public void Submit()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WebGpuCommandBuffer));
        if (_submitted) throw new InvalidOperationException("The command buffer has already been submitted.");
        if (_activePass != null) throw new InvalidOperationException("End the active render pass before submitting.");

        var commandBuffer = _runtime.FinishCommandEncoder(_encoder);
        _runtime.Submit(_queue, commandBuffer);
        _runtime.ReleaseCommandBuffer(commandBuffer);
        _runtime.ReleaseCommandEncoder(_encoder);
        _encoder = default;
        _submitted = true;
    }

    internal void EndPass(WebGpuRenderPass pass)
    {
        if (!ReferenceEquals(_activePass, pass))
            throw new InvalidOperationException("The render pass does not belong to this command buffer.");

        _runtime.EndRenderPass(pass.Handle);
        _activePass = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _activePass?.Dispose();

        if (!_submitted && _encoder.NativeHandle != 0)
        {
            _runtime.ReleaseCommandEncoder(_encoder);
            _encoder = default;
        }
    }
}
