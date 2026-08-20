using System.Reflection;

namespace Crowbar.Engine.Global;

/// <summary>
/// The type registry — the s&amp;box-style <c>TypeLibrary</c>. Exposed globally
/// as <see cref="GlobalNamespaces.TypeLibrary"/>. Wraps the engine's
/// <see cref="ComponentTypeRegistry"/>: game project assemblies register their
/// component types here, and the editor resolves, lists and instantiates
/// components by name without ever referencing game types directly.
/// </summary>
public sealed class TypeLibrary
{
    /// <summary>Every component type known to the runtime (engine + registered game project).</summary>
    public IReadOnlyList<Type> All => ComponentTypeRegistry.AllComponentTypes;

    /// <summary>Resolves a component type by its CLR name, or null.</summary>
    public Type? Resolve(string typeName) => ComponentTypeRegistry.Resolve(typeName);

    /// <summary>Registers the component types of a (re)loaded game project assembly.</summary>
    public void Register(Assembly assembly) => ComponentTypeRegistry.RegisterAssembly(assembly);

    /// <summary>Drops the component types of an assembly that was unloaded (project switch, full reload).</summary>
    public void Unregister(Assembly assembly) => ComponentTypeRegistry.UnregisterAssembly(assembly);

    /// <summary>
    /// The component types the entity can still attach: every registered type
    /// minus the ones already on it, ordered by name. The inspector's Add
    /// Component menu offers them.
    /// </summary>
    public IReadOnlyList<string> AttachableTo(Entity? entity)
    {
        if (entity is null)
            return [];
        return All
            .Where(type => entity.GetComponent(type) is null)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .Select(type => type.Name)
            .ToArray();
    }

    /// <summary>
    /// Attaches a component by name to the entity, so the editor never references
    /// game types. A malformed name, an already-present type or a throwing
    /// constructor is ignored (with a warning).
    /// </summary>
    public void AddComponent(Entity? entity, string typeName)
    {
        if (entity is null || string.IsNullOrEmpty(typeName))
            return;
        var type = Resolve(typeName);
        if (type is null || entity.GetComponent(type) is not null)
            return;
        try
        {
            if (Activator.CreateInstance(type) is Component component)
                entity.AddComponent(component);
        }
        catch (Exception ex)
        {
            GlobalNamespaces.Log.Warn($"[Inspector] Failed to add component '{typeName}': {ex.Message}");
        }
    }
}
