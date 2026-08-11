using System.Runtime.InteropServices;
using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;

namespace Crowbar.Engine;

public sealed class WebGpuDevice : IDisposable
{
    private readonly WebGpuRuntime _runtime;
    private nint _nativeHandle;

    internal WebGpuDevice(WebGpuRuntime runtime, WebGpuAdapter adapter)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(adapter);
        var deviceOptions = new DeviceDescriptor();

        var callback = PfnRequestDeviceCallback.From((status, device, msgPtr, _) =>
        {
            if (status is RequestDeviceStatus.Success)
            {
                _nativeHandle = (nint)device;
                Console.WriteLine("Retrieved WebGPU device.");
            }
            else
            {
                var message = Marshal.PtrToStringUTF8((IntPtr)msgPtr);
                Console.WriteLine($"Failed to create WebGPU device: {message}");
            }
        });

        unsafe
        {
            _runtime.Api.AdapterRequestDevice(adapter.UnsafeHandle, in deviceOptions, callback, null);
        }
    }

    internal nint NativeHandle => _nativeHandle;
    internal unsafe Device* UnsafeHandle => (Device*)_nativeHandle;

    public WebGpuQueue GetQueue()
    {
        unsafe
        {
            return WebGpuQueue.FromNative((nint)_runtime.Api.DeviceGetQueue(UnsafeHandle));
        }
    }

    internal unsafe Queue* GetUnsafeQueue() => (Queue*)GetQueue().NativeHandle;

    public void Dispose()
    {
        if (_nativeHandle != 0)
        {
            WebGpuNative.ReleaseDevice(_runtime.Api, _nativeHandle);
            _nativeHandle = 0;
        }
    }
}
