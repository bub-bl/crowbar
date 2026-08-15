using System.IO;

namespace Crowbar.FileSystems;

/// <summary>
/// OS-agnostic string path helpers, built on <see cref="FilePath"/>. They
/// replace <see cref="Path"/>'s string combiners for the engine's logical
/// content paths (<c>Shaders</c>, <c>Assets</c>, <c>Ui</c>, <c>Game</c>, ...)
/// and for assembling operating-system paths handed back to
/// <see cref="FileSystemService"/> (which re-normalizes them through
/// <see cref="Path.GetFullPath"/>).
/// </summary>
internal static class PathUtil
{
    /// <summary>Combines two path segments with the uniform separator.</summary>
    public static string Combine(string left, string right) => (new FilePath(left) / new FilePath(right)).FullName;

    /// <summary>Combines three path segments with the uniform separator.</summary>
    public static string Combine(string a, string b, string c) => (new FilePath(a) / new FilePath(b) / new FilePath(c)).FullName;

    /// <summary>Combines four path segments with the uniform separator.</summary>
    public static string Combine(string a, string b, string c, string d) => (new FilePath(a) / new FilePath(b) / new FilePath(c) / new FilePath(d)).FullName;

    /// <summary>Combines any number of path segments with the uniform separator.</summary>
    public static string Combine(params string[] paths)
    {
        if (paths.Length == 0)
            return string.Empty;

        FilePath result = paths[0];
        for (var i = 1; i < paths.Length; i++)
            result /= paths[i];
        return result.FullName;
    }

    /// <summary>True when the path's final component has an extension (a dot in it).</summary>
    public static bool HasExtension(string path) => !string.IsNullOrEmpty(new FilePath(path).GetExtensionWithDot());

    /// <summary>The final path component without its extension.</summary>
    public static string GetFileNameWithoutExtension(string path) => new FilePath(path).GetNameWithoutExtension() ?? string.Empty;

    /// <summary>Replaces the path's extension (with or without a leading dot).</summary>
    public static string ChangeExtension(string path, string extension) => new FilePath(path).ChangeExtension(extension).FullName;

    /// <summary>The platform's path-list separator (<c>;</c> on Windows, <c>:</c> elsewhere).</summary>
    public static char PathListSeparator => Path.PathSeparator;

    /// <summary>Characters that are invalid in a file name on this platform.</summary>
    public static char[] InvalidFileNameChars => Path.GetInvalidFileNameChars();
}
