using System.Runtime.CompilerServices;
using Crowbar.UI.Tests;

namespace Crowbar.UI.Tests;

internal static class TestAssemblySetup
{
    [ModuleInitializer]
    internal static void Initialize() => FileSystemSetup.Initialize();
}
