using Crowbar.Engine;
using Crowbar.Engine.Global;
using Crowbar.FileSystems;


namespace Crowbar.UI.Tests;

/// <summary>
/// Configures the process-wide services before any test runs. Tests exercise
/// the real engine/UI code paths (shaders, Razor compilation, script hot reload),
/// which read through <see cref="Crowbar.FileSystems.FileSystem.Content"/> (read-only)
/// and <see cref="Crowbar.FileSystems.FileSystem.Project"/>; the test output directory is
/// the content/project root, while OS temp paths resolve through the backend.
/// </summary>
public static class FileSystemSetup
{
    public static void Initialize()
    {
        var backend = ZioFileSystem.Physical();
        FileSystem.Configure(
            backend,
            new FileSystemService(new ReadOnlyFileSystem(backend), AppContext.BaseDirectory),
            new FileSystemService(backend, AppContext.BaseDirectory));

        // The engine's resource types (Model, Texture2D, AudioClip, Shader)
        // are discovered through their [AssetType] marker; production apps
        // register the assembly in Application.OnLoaded, and tests that load
        // resources directly (Model.Load, ...) need it registered on the
        // global library too.
        GlobalNamespaces.ResourceLibrary.Register(typeof(ResourceLibrary).Assembly);
    }
}
