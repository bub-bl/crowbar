using Crowbar.Engine;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// Performs component operations requested by the inspector without exposing
/// component-specific knowledge to <see cref="InspectorBridge"/>. Component
/// types are resolved from the runtime registry and inspected through
/// reflection, so game assemblies can contribute attachable components without
/// editor code changes.
/// </summary>
internal sealed class InspectorComponentService
{
    /// <summary>Returns the concrete component types the entity can still attach.</summary>
    public static IReadOnlyList<string> GetAttachableTypes(Entity? entity)
    {
        if (entity is null)
            return [];

        return TypeLibrary.All
            .Where(type => !type.IsAbstract && !type.ContainsGenericParameters)
            .Where(type => entity.GetComponent(type) is null)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .Select(type => type.Name)
            .ToArray();
    }

    /// <summary>Creates and attaches each queued component type to the selected entity.</summary>
    public static void AddComponents(Entity? entity, IReadOnlyList<string> typeNames)
    {
        if (entity is null)
            return;

        foreach (var typeName in typeNames)
            AddComponent(entity, typeName);
    }

    /// <summary>Removes the requested component types using the entity's generic runtime API.</summary>
    public static void RemoveComponents(Entity? entity, IReadOnlyList<string> typeNames)
    {
        if (entity is null)
            return;

        foreach (var typeName in typeNames)
        {
            try
            {
                var type = TypeLibrary.Resolve(typeName);
                if (type is null || entity.GetComponent(type) is null)
                    continue;

                entity.RemoveComponent(type);
            }
            catch (Exception ex)
            {
                Log.Warn($"[Inspector] Failed to remove component '{typeName}': {ex.Message}");
            }
        }
    }

    private static void AddComponent(Entity entity, string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return;

        var type = TypeLibrary.Resolve(typeName);
        if (type is null || type.IsAbstract || type.ContainsGenericParameters ||
            type.GetConstructor(Type.EmptyTypes) is null || entity.GetComponent(type) is not null)
            return;

        try
        {
            entity.AddComponent(type);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Inspector] Failed to add component '{typeName}': {ex.Message}");
        }
    }
}
