namespace Crowbar.Editor;

internal static class Program
{
    public static void Main(string[] args)
    {
        // Capture unhandled exceptions from any thread and route them to the
        // Log so the console UI can display them with full stack traces.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Error("Unhandled exception", ex ?? new Exception(e.ExceptionObject?.ToString()));
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

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
