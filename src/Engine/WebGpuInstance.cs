using Silk.NET.WebGPU;

namespace Crowbar.Engine;

public sealed class WebGpuInstance : IDisposable
{
    private readonly WebGpuRuntime _runtime;
    private nint _nativeHandle;

    internal WebGpuInstance(WebGpuRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        var instanceDescriptor = new InstanceDescriptor();
        unsafe
        {
            _nativeHandle = (nint)runtime.Api.CreateInstance(in instanceDescriptor);
        }

        Log.Info("Created WebGPU instance.");
    }

    internal nint NativeHandle => _nativeHandle;
    internal WebGpuRuntime Runtime => _runtime;

    internal unsafe Instance* UnsafeHandle => (Instance*)_nativeHandle;

    public void Dispose()
    {
        if (_nativeHandle != 0)
        {
            WebGpuNative.ReleaseInstance(_runtime.Api, _nativeHandle);
            _nativeHandle = 0;
        }
    }
}
