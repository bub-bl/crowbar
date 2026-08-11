using System.Text;

namespace Crowbar.Engine;

/// <summary>
/// Resolves <c>#include "path"</c> directives in WGSL sources into a single
/// flattened source, both for the GPU and for reflection. Includes are
/// resolved relative to the including file, then against the application's
/// Shaders/ directory — so a library file in Common/ can be referenced by any
/// shader regardless of where it lives. Cycles and runaway nesting are
/// rejected.
/// </summary>
internal static class ShaderPreprocessor
{
    private const int MaxIncludeDepth = 32;

    public static string Preprocess(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var builder = new StringBuilder();
        var stack = new Stack<string>();
        Resolve(Path.GetFullPath(filePath), builder, stack);
        return builder.ToString();
    }

    private static void Resolve(string filePath, StringBuilder builder, Stack<string> stack)
    {
        if (stack.Count >= MaxIncludeDepth)
            throw new InvalidOperationException($"Shader include depth exceeded at '{filePath}'.");

        if (stack.Contains(filePath))
            throw new InvalidOperationException(
                $"Circular #include detected: {string.Join(" -> ", stack.Reverse())} -> {filePath}");

        stack.Push(filePath);
        try
        {
            foreach (var rawLine in File.ReadLines(filePath))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("#include", StringComparison.Ordinal))
                {
                    builder.AppendLine(rawLine);
                    continue;
                }

                var include = ParseInclude(line, filePath);
                var directory = Path.GetDirectoryName(filePath)!;
                var candidate = Path.Combine(directory, include);
                if (!File.Exists(candidate))
                {
                    candidate = Path.Combine(AppContext.BaseDirectory, "Shaders", include);
                    if (!File.Exists(candidate))
                    {
                        throw new FileNotFoundException(
                            $"Shader '{filePath}' includes '{include}', which was not found next to it or in the Shaders directory.",
                            include);
                    }
                }

                Resolve(Path.GetFullPath(candidate), builder, stack);
            }
        }
        finally
        {
            stack.Pop();
        }
    }

    private static string ParseInclude(string line, string filePath)
    {
        var start = line.IndexOf('"');
        var end = line.LastIndexOf('"');
        if (start < 0 || end <= start)
            throw new FormatException($"Malformed #include in '{filePath}': '{line}'.");
        return line[(start + 1)..end];
    }
}
