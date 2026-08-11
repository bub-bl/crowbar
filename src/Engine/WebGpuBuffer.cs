using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;

namespace Crowbar.Engine;

/// <summary>
/// WebGPU-backed <see cref="IBuffer"/>. Writes go through the queue
/// (QueueWriteBuffer), so they are ordered with the submitted commands.
/// </summary>
public sealed unsafe class WebGpuBuffer : IBuffer
{
    private readonly WebGpuRuntime _runtime;
    private readonly Queue* _queue;
    private bool _disposed;

    internal Silk.NET.WebGPU.Buffer* Buffer { get; private set; }

    public ulong Size { get; }

    private WebGpuBuffer(WebGpuRuntime runtime, Queue* queue, Silk.NET.WebGPU.Buffer* buffer, ulong size)
    {
        _runtime = runtime;
        _queue = queue;
        Buffer = buffer;
        Size = size;
    }

    internal static WebGpuBuffer Create(
        WebGpuRuntime runtime,
        WebGpuDevice device,
        WebGpuQueue queue,
        BufferDescription description)
    {
        var descriptor = new BufferDescriptor
        {
            Size = description.Size,
            Usage = WebGpuNative.ToNative(description.Usage),
            MappedAtCreation = false
        };
        var buffer = runtime.Api.DeviceCreateBuffer(device.UnsafeHandle, in descriptor);
        if (buffer == null)
            throw new InvalidOperationException("WebGPU could not create the buffer.");

        return new WebGpuBuffer(runtime, (Queue*)queue.NativeHandle, buffer, description.Size);
    }

    public void Write<T>(in T data, ulong offset = 0) where T : unmanaged
    {
        if (_disposed || Buffer == null)
            return;

        T copy = data;
        _runtime.Api.QueueWriteBuffer(_queue, Buffer, offset, &copy, (nuint)sizeof(T));
    }

    public void Write(ReadOnlySpan<byte> data, ulong offset = 0)
    {
        if (_disposed || Buffer == null)
            return;

        fixed (byte* ptr = data)
            _runtime.Api.QueueWriteBuffer(_queue, Buffer, offset, ptr, (nuint)data.Length);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (Buffer != null)
        {
            _runtime.Api.BufferDestroy(Buffer);
            _runtime.Api.BufferRelease(Buffer);
            Buffer = null;
        }
    }
}
