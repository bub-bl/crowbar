using System.Runtime.InteropServices;
using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;

namespace Crowbar.Engine;

public sealed class WebGpuAdapter : IDisposable
{
    private readonly WebGpuRuntime _runtime;
    private nint _nativeHandle;

    public WebGpuAdapter(WebGpuRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        var adapterOptions = new RequestAdapterOptions
        {
            BackendType = BackendType.Undefined,
            PowerPreference = PowerPreference.HighPerformance
        };

        var callback = PfnRequestAdapterCallback.From((status, adapter, msgPtr, _) =>
        {
            if (status is RequestAdapterStatus.Success)
            {
                _nativeHandle = (nint)adapter;
                Log.Info("WebGPU adapter selected.");
                return;
            }

            string message = Marshal.PtrToStringUTF8((IntPtr)msgPtr) ?? "Unknown adapter error.";
            Log.Warn($"WebGPU adapter selection failed: {message}");
        });

        unsafe
        {
            _runtime.Api.InstanceRequestAdapter(_runtime.Instance.UnsafeHandle, in adapterOptions, callback, null);
        }

        if (_nativeHandle == 0)
            throw new InvalidOperationException("WebGPU did not return an adapter.");
    }

    public WebGpuAdapter(WebGpuRuntime runtime, WebGpuSurface surface)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        var adapterOptions = new RequestAdapterOptions
        {
            BackendType = BackendType.Undefined, // Autodetect best backend (Vulkan / D3D12 / Metal)
            PowerPreference = PowerPreference.HighPerformance
        };

        unsafe
        {
            adapterOptions.CompatibleSurface = (Surface*)surface.NativeHandle;
        }

        var callback = PfnRequestAdapterCallback.From((status, adapter, msgPtr, userDataPtr) =>
        {
            if (status is RequestAdapterStatus.Success)
            {
                _nativeHandle = (nint)adapter;
                Log.Info("Retrieved WebGPU adapter.");
                return;
            }

            var message = Marshal.PtrToStringUTF8((IntPtr)msgPtr);
            Log.Warn($"Failed to create WebGPU adapter: {message}");
        });

        unsafe
        {
            _runtime.Api.InstanceRequestAdapter(_runtime.Instance.UnsafeHandle, in adapterOptions, callback, null);
        }
        Log.Info("Created WebGPU adapter.");
    }

    public WebGpuDevice CreateDevice()
    {
        return new WebGpuDevice(_runtime, this);
    }

    internal nint NativeHandle => _nativeHandle;
    internal unsafe Adapter* UnsafeHandle => (Adapter*)_nativeHandle;

    public void Dispose()
    {
        if (_nativeHandle != 0)
        {
            WebGpuNative.ReleaseAdapter(_runtime.Api, _nativeHandle);
            _nativeHandle = 0;
        }
    }
}
