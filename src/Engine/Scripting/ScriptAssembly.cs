using System.Reflection;
using System.Runtime.Loader;

namespace Crowbar.Engine.Scripting;

/// <summary>
/// A compiled set of game script files, loaded into its own collectible
/// <see cref="AssemblyLoadContext"/>. Each reload generation gets a fresh
/// context so the previous one can be unloaded once nothing references it
/// (preserved state keeps old instances alive, which is expected).
/// </summary>
public sealed class ScriptAssembly : IDisposable
{
    private readonly AssemblyLoadContext _loadContext;
    private bool _disposed;

    internal ScriptAssembly(AssemblyLoadContext loadContext, Assembly assembly, string? directory, IReadOnlyList<string> files)
    {
        _loadContext = loadContext;
        Assembly = assembly;
        Directory = directory;
        Files = files;
        TypesByFullName = assembly.GetTypes()
            .Where(t => t.FullName is not null)
            .ToDictionary(t => t.FullName!, StringComparer.Ordinal);
    }

    /// <summary>The loaded script assembly.</summary>
    public Assembly Assembly { get; }

    /// <summary>Root directory the scripts were compiled from, or null when compiled from loose files.</summary>
    public string? Directory { get; }

    /// <summary>Full paths of the source files that make up this assembly.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>Every type in the assembly, keyed by full name (the key used for hot reload type mapping).</summary>
    public IReadOnlyDictionary<string, Type> TypesByFullName { get; }

    /// <summary>Finds a type by its full name (e.g. "Game.Player"), or null.</summary>
    public Type? GetType(string fullName) => TypesByFullName.GetValueOrDefault(fullName);

    /// <summary>
    /// Creates an instance of the named type using its parameterless
    /// constructor. Returns null when the type does not exist, is abstract,
    /// is a generic definition, or has no parameterless constructor.
    /// </summary>
    public object? CreateInstance(string fullName)
    {
        var type = GetType(fullName);
        if (type is null || type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
            return null;
        var constructor = type.GetConstructor(Type.EmptyTypes);
        return constructor is null ? null : Activator.CreateInstance(type);
    }

    /// <summary>Requests the unload of this generation's load context (best effort).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _loadContext.Unload();
            GC.Collect();
        }
        catch (Exception ex)
        {
            // Collectible-context unloading is best effort; state preserved by
            // the hot reload keeps the previous generation alive on purpose.
            Log.Warn($"[Scripting] Assembly unload failed: {ex.Message}");
        }
    }
}
