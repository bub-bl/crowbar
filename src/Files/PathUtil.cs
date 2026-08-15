using Zio;

namespace Crowbar.Files;

/// <summary>
/// OS-agnostic path helpers built on <see cref="UPath"/>. Paths use the uniform
/// <c>/</c> separator and are normalized (trailing separators and <c>.</c>/<c>..</c>
/// segments are squashed), so they are stable dictionary keys and behave the same
/// on every platform. These replace the <see cref="System.IO.Path"/> string helpers
/// for the engine's logical content paths (Shaders, Assets, Ui, Game, ...).
/// </summary>
public static class PathUtil
{
    /// <summary>Combines two path segments with the uniform separator.</summary>
    public static string Combine(string left, string right) => ((UPath)left / (UPath)right).FullName;

    /// <summary>Combines three path segments with the uniform separator.</summary>
    public static string Combine(string a, string b, string c) => ((UPath)a / (UPath)b / (UPath)c).FullName;

    /// <summary>Combines four path segments with the uniform separator.</summary>
    public static string Combine(string a, string b, string c, string d) => ((UPath)a / (UPath)b / (UPath)c / (UPath)d).FullName;

    /// <summary>Combines any number of path segments with the uniform separator.</summary>
    public static string Combine(params string[] paths)
    {
        if (paths.Length == 0)
            return string.Empty;
        var result = (UPath)paths[0];
        for (var i = 1; i < paths.Length; i++)
            result = result / (UPath)paths[i];
        return result.FullName;
    }

    /// <summary>True when the path has an extension (a final component with a dot in it).</summary>
    public static bool HasExtension(string path) => !string.IsNullOrEmpty(((UPath)path).GetExtensionWithDot());

    /// <summary>The final path component, including its extension.</summary>
    public static string GetFileName(string path) => ((UPath)path).GetName();

    /// <summary>The final path component without its extension.</summary>
    public static string GetFileNameWithoutExtension(string path) => ((UPath)path).GetNameWithoutExtension() ?? string.Empty;

    /// <summary>The directory portion of the path.</summary>
    public static string GetDirectoryName(string path) => ((UPath)path).GetDirectory().FullName;

    /// <summary>Replaces the path's extension (with or without a leading dot).</summary>
    public static string ChangeExtension(string path, string extension) => ((UPath)path).ChangeExtension(extension).FullName;

    /// <summary>The normalized form of the path (uniform separator, no trailing slash).</summary>
    public static string Normalize(string path) => ((UPath)path).FullName;

    /// <summary>The platform's path-list separator (<c>;</c> on Windows, <c>:</c> elsewhere).</summary>
    public static char PathListSeparator => System.IO.Path.PathSeparator;

    /// <summary>Characters that are invalid in a file name on this platform.</summary>
    public static char[] InvalidFileNameChars => System.IO.Path.GetInvalidFileNameChars();
}
