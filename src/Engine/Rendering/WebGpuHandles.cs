namespace Crowbar.Engine.Rendering;

/// <summary>Opaque safe handle for a WebGPU surface.</summary>
public readonly struct WebGpuSurface
{
    internal nint NativeHandle { get; }

    internal WebGpuSurface(nint nativeHandle) => NativeHandle = Require(nativeHandle);

    internal static WebGpuSurface FromNative(nint nativeHandle) => new(nativeHandle);

    private static nint Require(nint handle) => handle == 0
        ? throw new ArgumentException("A valid WebGPU surface handle is required.", nameof(handle))
        : handle;
}

/// <summary>Opaque safe handle for a WebGPU queue.</summary>
public readonly struct WebGpuQueue
{
    internal nint NativeHandle { get; }

    internal WebGpuQueue(nint nativeHandle) => NativeHandle = Require(nativeHandle);

    internal static WebGpuQueue FromNative(nint nativeHandle) => new(nativeHandle);

    private static nint Require(nint handle) => handle == 0
        ? throw new ArgumentException("A valid WebGPU queue handle is required.", nameof(handle))
        : handle;
}

/// <summary>Opaque safe handle for a WebGPU render pass encoder.</summary>
public readonly struct WebGpuRenderPassEncoder
{
    internal nint NativeHandle { get; }

    internal WebGpuRenderPassEncoder(nint nativeHandle) => NativeHandle = Require(nativeHandle);

    internal static WebGpuRenderPassEncoder FromNative(nint nativeHandle) => new(nativeHandle);

    private static nint Require(nint handle) => handle == 0
        ? throw new ArgumentException("A valid WebGPU render pass encoder handle is required.", nameof(handle))
        : handle;
}

internal readonly struct WebGpuNativeCommandEncoder
{
    internal nint NativeHandle { get; }
    internal WebGpuNativeCommandEncoder(nint nativeHandle) => NativeHandle = nativeHandle;
}

internal readonly struct WebGpuNativeCommandBuffer
{
    internal nint NativeHandle { get; }
    internal WebGpuNativeCommandBuffer(nint nativeHandle) => NativeHandle = nativeHandle;
}
