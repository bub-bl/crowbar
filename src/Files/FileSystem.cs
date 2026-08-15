namespace Crowbar.Files;

/// <summary>
/// The engine's filesystems, exposed as process-wide static accessors — the
/// s&amp;box-style entry point. The host configures them once at startup; the
/// engine (and game scripts) read all content through them.
///
/// <code>
/// FileSystem.Mounted = ZioFileSystem.Physical();                    // raw backend
/// FileSystem.Content = new FileSystemService(FileSystem.Mounted, ...); // logical content view
/// </code>
/// </summary>
public static class FileSystem
{
    private static IFileSystem? _mounted;
    private static FileSystemService? _content;

    /// <summary>
    /// The raw mounted backend (physical disk, memory, archive, ...). This is
    /// the filesystem <see cref="Content"/> resolves against; access it directly
    /// for backend-coordinate paths (e.g. an absolute OS path).
    /// </summary>
    public static IFileSystem Mounted
    {
        get => _mounted ?? throw new InvalidOperationException(
            "FileSystem.Mounted has not been configured. Assign a backend at startup (e.g. ZioFileSystem.Physical() in Crowbar.Files.Zio).");
        set => _mounted = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The engine's content view: logical paths (<c>Shaders/Pbr.wgsl</c>,
    /// <c>/Game/DemoGamemode.cs</c>, ...) resolved through the configured mounts
    /// and content root. This is what the engine reads from.
    /// </summary>
    public static FileSystemService Content
    {
        get => _content ?? throw new InvalidOperationException(
            "FileSystem.Content has not been configured. Assign it at startup with `new FileSystemService(FileSystem.Mounted, contentRoot, mounts)`.");
        set => _content = value ?? throw new ArgumentNullException(nameof(value));
    }
}
