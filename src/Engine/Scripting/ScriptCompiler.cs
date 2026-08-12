using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Crowbar.Engine.Scripting;

/// <summary>
/// Compiles game scripts into in-memory assemblies using a persistent,
/// incremental <see cref="CSharpCompilation"/> per assembly name. Recompiling
/// the same set of files reuses the parsed trees of unchanged files (Roslyn
/// caches per-tree analysis), so editing one file costs only that file's
/// parse and emit — the basis of fast hot reloads.
/// </summary>
public sealed class ScriptCompiler
{
    private static readonly object PlatformReferencesLock = new();
    private static IReadOnlyList<PortableExecutableReference>? _platformReferences;

    private static readonly CSharpCompilationOptions CompilationOptions = new(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        allowUnsafe: true);

    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    private readonly IReadOnlyList<PortableExecutableReference> _references;
    private readonly ConcurrentDictionary<string, ProjectCompilation> _projects = new(StringComparer.Ordinal);

    /// <param name="references">Assemblies the scripts can reference (typically the engine and editor
    /// assemblies). The trusted platform assembly set is always included.</param>
    public ScriptCompiler(IEnumerable<Assembly>? references = null)
    {
        _references = (references ?? [])
            .Where(a => !string.IsNullOrEmpty(a.Location))
            .Distinct()
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Concat(PlatformReferences)
            .ToArray();
    }

    /// <summary>Compiles every *.cs file under <paramref name="directory"/> (recursively).</summary>
    public ScriptAssembly CompileDirectory(string directory, string assemblyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyName);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Script directory not found: {directory}");
        var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Compile(files, assemblyName);
    }

    /// <summary>Compiles the given source files. Recompiling with the same assembly name is incremental.</summary>
    public ScriptAssembly Compile(IEnumerable<string> sourceFiles, string assemblyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyName);
        var files = sourceFiles.Select(Path.GetFullPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        var project = _projects.GetOrAdd(assemblyName, name => new ProjectCompilation(name, _references));
        var il = project.Emit(files);
        return LoadAssembly(assemblyName, il, files);
    }

    private static ScriptAssembly LoadAssembly(string assemblyName, byte[] il, IReadOnlyList<string> files)
    {
        var loadContext = new AssemblyLoadContext($"Crowbar.Script.{assemblyName}.{Guid.NewGuid():N}", isCollectible: true);
        using var stream = new MemoryStream(il);
        var assembly = loadContext.LoadFromStream(stream);
        var directory = files.Count > 0 ? Path.GetDirectoryName(files[0]) : null;
        return new ScriptAssembly(loadContext, assembly, directory, files);
    }

    private static IReadOnlyList<PortableExecutableReference> PlatformReferences
    {
        get
        {
            lock (PlatformReferencesLock)
            {
                return _platformReferences ??= ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Where(File.Exists)
                    .Select(path => MetadataReference.CreateFromFile(path))
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// Holds the live <see cref="CSharpCompilation"/> for one assembly name.
    /// Files are re-parsed only when their write time changes; unchanged trees
    /// keep their instances so Roslyn's incremental engine reuses them.
    /// </summary>
    private sealed class ProjectCompilation
    {
        private readonly object _gate = new();
        private readonly string _assemblyName;
        private readonly IReadOnlyList<MetadataReference> _references;
        private readonly Dictionary<string, (long WriteTime, SyntaxTree Tree)> _trees = new(StringComparer.OrdinalIgnoreCase);
        private CSharpCompilation? _compilation;

        public ProjectCompilation(string assemblyName, IReadOnlyList<MetadataReference> references)
        {
            _assemblyName = assemblyName;
            _references = references;
        }

        public byte[] Emit(IReadOnlyList<string> files)
        {
            lock (_gate)
            {
                var fileSet = files.ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Drop trees for files that no longer exist.
                foreach (var gone in _trees.Keys.Where(k => !fileSet.Contains(k)).ToArray())
                    _trees.Remove(gone);

                // Re-parse only the files whose content may have changed.
                foreach (var file in fileSet)
                {
                    var writeTime = File.GetLastWriteTimeUtc(file).Ticks;
                    if (_trees.TryGetValue(file, out var existing) && existing.WriteTime == writeTime)
                        continue;
                    var text = File.ReadAllText(file);
                    var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path: file);
                    _trees[file] = (writeTime, tree);
                }

                if (_compilation is null)
                {
                    _compilation = CSharpCompilation.Create(
                        _assemblyName, _trees.Values.Select(t => t.Tree), _references, CompilationOptions);
                }
                else
                {
                    // Incremental: add new files, drop deleted ones, replace the
                    // trees whose content changed (unchanged tree instances are
                    // reused so Roslyn's incremental engine skips their analysis).
                    var currentTrees = _compilation.SyntaxTrees.ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase);
                    foreach (var removed in currentTrees.Keys.Where(k => !_trees.ContainsKey(k)).ToArray())
                        _compilation = _compilation.RemoveSyntaxTrees(currentTrees[removed]);
                    foreach (var (file, entry) in _trees)
                    {
                        if (!currentTrees.TryGetValue(file, out var oldTree))
                            _compilation = _compilation.AddSyntaxTrees(entry.Tree);
                        else if (!ReferenceEquals(oldTree, entry.Tree))
                            _compilation = _compilation.ReplaceSyntaxTree(oldTree, entry.Tree);
                    }
                }

                using var stream = new MemoryStream();
                var result = _compilation.Emit(stream);
                if (!result.Success)
                {
                    var errors = result.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.ToString());
                    throw new ScriptCompilationException(
                        $"Script compilation failed ({errors.Count()} error(s)):\n{string.Join("\n", errors)}");
                }

                return stream.ToArray();
            }
        }
    }
}
