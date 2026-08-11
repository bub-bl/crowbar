using Silk.NET.SDL;

namespace Crowbar.Engine.Platform;

/// <summary>
/// SDL2 platform: initializes SDL once and creates <see cref="SdlWindow"/>
/// windows backed by real SDL windows. All input (keyboard, mouse, text,
/// wheel) flows through SDL, so the platform is fully cross-platform.
/// </summary>
public sealed class SdlPlatform : IPlatform
{
    private readonly Sdl _sdl;
    private bool _disposed;

    public SdlPlatform()
    {
        _sdl = Sdl.GetApi();
        _sdl.SetMainReady();
        if (_sdl.Init(Sdl.InitVideo) < 0)
            throw new InvalidOperationException($"SDL initialization failed: {_sdl.GetErrorS()}");
    }

    public IWindow CreateWindow(WindowOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new SdlWindow(_sdl, options);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _sdl.Quit();
        _sdl.Dispose();
        _disposed = true;
    }
}
