using Crowbar.FileSystems;

namespace Crowbar.Engine.Scripting;

/// <summary>
/// Filters the game source enumeration so the runtime never compiles or watches
/// build artifacts. The game project is a real .NET project (Game/Game.csproj)
/// whose bin/ and obj/ directories live under the watched folder; generated
/// files there (AssemblyInfo, source-generator output, ...) must not become part
/// of the hot-reloaded script assembly and must not trigger reloads.
/// </summary>
internal static class ScriptSourceFilter
{
    public static bool IsBuildArtifact(FilePath path)
    {
        var segments = path.FullName.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i] is "bin" or "obj")
                return true;
        }

        return false;
    }
}