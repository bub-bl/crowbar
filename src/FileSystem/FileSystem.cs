namespace Crowbar.FileSystems;

/// <summary>
/// The engine's filesystems, exposed as process-wide static accessors — the
/// s&amp;box-style entry point. The host configures them once at startup via
/// <see cref="Configure"/>.
///
/// Game code sees exactly two filesystems:
/// <list type="bullet">
/// <item><see cref="Content"/> — the base Crowbar content (engine shaders,
/// assets, editor UI), read-only;</item>
/// <item><see cref="Project"/> — the active gamemode project, read-write.</item>
/// </list>
///
/// <see cref="Mounted"/> (the raw backend both views resolve against) is
/// host-internal and not visible to game code.
/// </summary>
public static class FileSystem
{
    private static IFileSystem? _mounted;
    private static FileSystemService? _content;
    private static FileSystemService? _project;

    /// <summary>
    /// The raw backend (physical disk, memory, archive, ...) both views resolve
    /// against. Host/tooling only — not visible to game code.
    /// </summary>
    internal static IFileSystem Mounted => _mounted ?? throw new InvalidOperationException(
        "FileSystem has not been configured. Call FileSystem.Configure(backend, content, project) at startup.");

    /// <summary>The base Crowbar content (engine shaders, assets, editor UI), read-only.</summary>
    public static FileSystemService Content => _content ?? throw new InvalidOperationException(
        "FileSystem.Content has not been configured. Call FileSystem.Configure(backend, content, project) at startup.");

    /// <summary>The active gamemode project, read-write.</summary>
    public static FileSystemService Project => _project ?? throw new InvalidOperationException(
        "FileSystem.Project has not been configured. Call FileSystem.Configure(backend, content, project) at startup.");

    /// <summary>Configures the three filesystems once, from the host's startup code (or a test fixture).</summary>
    internal static void Configure(IFileSystem backend, FileSystemService content, FileSystemService project)
    {
        _mounted = backend ?? throw new ArgumentNullException(nameof(backend));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _project = project ?? throw new ArgumentNullException(nameof(project));
    }
}
