namespace Crowbar.Editor;

internal static class Program
{
    public static void Main(string[] args)
    {
        var projectFile = ResolveProjectArg(args);
        Editor.ConfigureFileSystem(projectFile);
        new Editor(projectFile).Run();
    }

    /// <summary>
    /// The first command-line argument is the project file to open (the path
    /// Windows passes when a <c>.crproj</c> is double-clicked), or null when
    /// the editor starts bare. Anything else is ignored: it is not a project
    /// file, so the editor falls back to its default demo project.
    /// </summary>
    private static string? ResolveProjectArg(string[] args)
    {
        if (args is not { Length: > 0 } || string.IsNullOrWhiteSpace(args[0]))
            return null;
        var path = Path.GetFullPath(args[0]);
        return path.EndsWith(".crproj", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            ? path
            : null;
    }
}
