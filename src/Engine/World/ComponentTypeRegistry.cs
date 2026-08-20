using System.Reflection;

namespace Crowbar.Engine;

/// <summary>
/// Maps the short type names used by the level file format (e.g. "MeshRenderer",
/// "PointLight") back to their runtime <see cref="Component"/> types. The
/// engine's own components are indexed lazily by scanning its assembly; game
/// assemblies add theirs through <see cref="RegisterAssembly"/> (the editor does
/// this when it loads the game project, so its components become attachable).
/// Names that resolve to nothing are skipped by the deserializer — that is what
/// makes a level file tolerant of components that were renamed or removed since
/// it was saved.
/// </summary>
public static class ComponentTypeRegistry
{
    private static Dictionary<string, Type>? _index;
    private static readonly Dictionary<string, Assembly> Owner = new(StringComparer.Ordinal);
    private static readonly Lock Lock = new();

    /// <summary>Resolves a component type by its short name, or null when unknown.</summary>
    public static Type? Resolve(string shortName)
    {
        if (string.IsNullOrEmpty(shortName))
            return null;

        var index = _index ??= BuildIndex();
        return index.GetValueOrDefault(shortName);
    }

    /// <summary>
    /// Registers a component type so its short name is recognized when loading
    /// levels and offered by the editor's Add Component list. The engine's own
    /// components are already indexed; this is for individual game component
    /// types.
    /// </summary>
    public static void Register(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!typeof(Component).IsAssignableFrom(type))
            throw new ArgumentException($"'{type.Name}' is not a Component type.", nameof(type));

        lock (Lock)
        {
            var index = _index ??= BuildIndex();
            index[type.Name] = type;
            Owner[type.Name] = type.Assembly;
        }
    }

    /// <summary>
    /// Registers every concrete, instantiable <see cref="Component"/> type of an
    /// assembly (the editor calls this after loading or hot-reloading the game
    /// project, so its components become attachable).
    /// </summary>
    public static void RegisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (Lock)
        {
            var index = _index ??= BuildIndex();
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.ContainsGenericParameters || !typeof(Component).IsAssignableFrom(type))
                    continue;
                if (type.GetConstructor(Type.EmptyTypes) is null)
                    continue;
                index[type.Name] = type;
                Owner[type.Name] = assembly;
            }
        }
    }

    /// <summary>
    /// Removes every component type contributed by an assembly (called on full
    /// hot reload so types of the unloaded generation no longer resolve).
    /// </summary>
    public static void UnregisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (Lock)
        {
            if (_index is null)
                return;
            foreach (var name in Owner.Where(kv => kv.Value == assembly).Select(kv => kv.Key).ToArray())
            {
                _index.Remove(name);
                Owner.Remove(name);
            }
        }
    }

    /// <summary>
    /// Every concrete, instantiable component type currently known: the engine's
    /// own components plus the registered game project's. The editor uses this
    /// to build its Add Component list.
    /// </summary>
    public static IReadOnlyList<Type> AllComponentTypes
    {
        get
        {
            var index = _index ??= BuildIndex();
            return index.Values
                .Where(t => !t.IsAbstract && !t.ContainsGenericParameters && t.GetConstructor(Type.EmptyTypes) is not null)
                .ToArray();
        }
    }

    private static Dictionary<string, Type> BuildIndex()
    {
        var index = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in typeof(Component).Assembly.GetTypes())
        {
            if (type.IsAbstract || type.ContainsGenericParameters || !typeof(Component).IsAssignableFrom(type))
                continue;
            if (type.GetConstructor(Type.EmptyTypes) is null)
                continue;
            index.TryAdd(type.Name, type);
        }

        return index;
    }
}