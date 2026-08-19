namespace Crowbar.Engine;

/// <summary>
/// Maps the short type names used by the level file format (e.g. "MeshRenderer",
/// "PointLight") back to their runtime <see cref="Component"/> types. The
/// engine's own components are indexed lazily by scanning its assembly; game
/// assemblies can add theirs through <see cref="Register"/>. Names that resolve
/// to nothing are skipped by the deserializer — that is what makes a level file
/// tolerant of components that were renamed or removed since it was saved.
/// </summary>
public static class ComponentTypeRegistry
{
    private static Dictionary<string, Type>? _index;
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
    /// levels. The engine's own components are already indexed; this is for
    /// game assemblies that ship their own components.
    /// </summary>
    public static void Register(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!typeof(Component).IsAssignableFrom(type))
            throw new ArgumentException($"'{type.Name}' is not a Component type.", nameof(type));

        lock (Lock)
        {
            _index ??= BuildIndex();
            _index[type.Name] = type;
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
