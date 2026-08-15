using System.Runtime.CompilerServices;
using Crowbar.Files;
using Crowbar.Files.Zio;

namespace Crowbar.UI.Tests;

/// <summary>
/// Configures the process-wide filesystem before any test runs. Tests exercise
/// the real engine/UI code paths (shaders, Razor compilation, script hot reload),
/// which read through <see cref="Crowbar.Files.FileSystem.Content"/>; the test output
/// directory is the content root (shaders and assets are copied there by the
/// engine project), while OS temp paths resolve through the backend directly.
/// </summary>
internal static class FileSystemSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        FileSystem.Mounted = ZioFileSystem.Physical();
        FileSystem.Content = new FileSystemService(FileSystem.Mounted, AppContext.BaseDirectory);
    }
}
