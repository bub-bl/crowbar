using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Crowbar.Files;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Zio;

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
        => CompileDirectory(FileSystemService.Default.ToUPath(directory), assemblyName);

    public ScriptAssembly CompileDirectory(UPath directory, string assemblyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyName);
        var fs = FileSystemService.Default;
        if (!fs.DirectoryExists(directory))
            throw new DirectoryNotFoundException($"Script directory not found: {directory}");
        var files = fs.EnumerateFiles(directory, "*.cs", recursive: true).ToArray();
        return CompileCore(files, assemblyName);
    }

    /// <summary>Compiles the given source files. Recompiling with the same assembly name is incremental.</summary>
    public ScriptAssembly Compile(IEnumerable<string> sourceFiles, string assemblyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyName);
        var fs = FileSystemService.Default;
        var files = sourceFiles.Select(fs.ToUPath).ToArray();
        return CompileCore(files, assemblyName);
    }

    private ScriptAssembly CompileCore(IReadOnlyList<UPath> files, string assemblyName)
    {
        var ordered = files
            .DistinctBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var project = _projects.GetOrAdd(assemblyName, name => new ProjectCompilation(name, _references));
        var il = project.Emit(ordered);
        return LoadAssembly(assemblyName, il, ordered);
    }

    /// <summary>
    /// Returns the syntax trees of the last compilation for an assembly name,
    /// or false when the assembly has never been compiled. Used by the hot
    /// reload to classify a change (body-only vs structural) against the
    /// previous generation.
    /// </summary>
    public bool TryGetLastSyntaxTrees(string assemblyName, out IReadOnlyDictionary<string, SyntaxTree> trees)
    {
        if (_projects.TryGetValue(assemblyName, out var project))
            return project.TryGetTrees(out trees);
        trees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        return false;
    }

    private static ScriptAssembly LoadAssembly(string assemblyName, byte[] il, IReadOnlyList<UPath> files)
    {
        var loadContext = new AssemblyLoadContext($"Crowbar.Script.{assemblyName}.{Guid.NewGuid():N}", isCollectible: true);
        using var stream = new MemoryStream(il);
        var assembly = loadContext.LoadFromStream(stream);
        var directory = files.Count > 0 ? files[0].GetDirectory().FullName : null;
        return new ScriptAssembly(loadContext, assembly, directory, files.Select(file => file.FullName).ToArray());
    }

    private static IReadOnlyList<PortableExecutableReference> PlatformReferences
    {
        get
        {
            lock (PlatformReferencesLock)
            {
                return _platformReferences ??= ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
                    .Split(PathUtil.PathListSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Where(FileSystemService.Default.FileExists)
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

        public bool TryGetTrees(out IReadOnlyDictionary<string, SyntaxTree> trees)
        {
            lock (_gate)
            {
                if (_trees.Count == 0)
                {
                    trees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
                    return false;
                }

                trees = _trees.ToDictionary(kv => kv.Key, kv => kv.Value.Tree, StringComparer.OrdinalIgnoreCase);
                return true;
            }
        }

        public byte[] Emit(IReadOnlyList<UPath> files)
        {
            lock (_gate)
            {
                var fs = FileSystemService.Default;
                var fileSet = files.Select(file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Drop trees for files that no longer exist.
                foreach (var gone in _trees.Keys.Where(k => !fileSet.Contains(k)).ToArray())
                    _trees.Remove(gone);

                // Re-parse only the files whose content may have changed.
                foreach (var file in files)
                {
                    var key = file.FullName;
                    var writeTime = fs.GetLastWriteTimeUtc(file).Ticks;
                    if (_trees.TryGetValue(key, out var existing) && existing.WriteTime == writeTime)
                        continue;
                    var text = fs.ReadAllText(file);
                    var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path: key);
                    _trees[key] = (writeTime, tree);
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
