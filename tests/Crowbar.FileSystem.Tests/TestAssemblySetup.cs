using System.Runtime.CompilerServices;
using Crowbar.UI.Tests;

namespace Crowbar.FileSystem.Tests;

internal static class TestAssemblySetup
{
    [ModuleInitializer]
    internal static void Initialize() => FileSystemSetup.Initialize();
}
