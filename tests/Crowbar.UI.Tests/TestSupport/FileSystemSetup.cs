using System.Runtime.CompilerServices;
using Crowbar.FileSystems;


namespace Crowbar.UI.Tests;

/// <summary>
/// Configures the process-wide filesystem before any test runs. Tests exercise
/// the real engine/UI code paths (shaders, Razor compilation, script hot reload),
/// which read through <see cref="Crowbar.FileSystems.FileSystem.Content"/> (read-only)
/// and <see cref="Crowbar.FileSystems.FileSystem.Project"/>; the test output directory is
/// the content/project root, while OS temp paths resolve through the backend.
/// </summary>
internal static class FileSystemSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var backend = ZioFileSystem.Physical();
        FileSystem.Configure(
            backend,
            new FileSystemService(new ReadOnlyFileSystem(backend), AppContext.BaseDirectory),
            new FileSystemService(backend, AppContext.BaseDirectory));
    }
}
