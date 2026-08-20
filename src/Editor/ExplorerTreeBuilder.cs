using System.Reflection;
using Crowbar.Engine;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// Builds the flat tree snapshot the Explorer panel displays from the live
/// world — the analog of an outliner: a world root, one folder per level, and
/// entities nested under their transform parent. The editor host runs it every
/// frame (the only place that owns the world) and publishes the result to
/// <see cref="EditorExplorerState"/>, which the UI-only Explorer panel reads.
/// </summary>
public static class ExplorerTreeBuilder
{
    /// <summary>Stable id of the world root folder (levels and unleveled entities nest under it).</summary>
    public static readonly Guid WorldRootId = Guid.Parse("b7f10c9d-9b3e-4f6a-8c1d-2a5e7f0d3c41");

    public static void Publish(World world, Entity? selection)
    {
        var nodes = new List<EditorExplorerState.TreeNode>
        {
            new("World", WorldRootId, null, "Solar/map/Bold/globe", IsFolder: true)
        };

        // Unleveled (world-only) entities first, then one folder per level.
        foreach (var entity in world.Entities)
        {
            if (entity.Level is null && entity.IsValid)
                AddEntity(nodes, entity, WorldRootId);
        }

        foreach (var level in world.Levels)
        {
            nodes.Add(new EditorExplorerState.TreeNode(level.Name, level.Id, WorldRootId, string.Empty, IsFolder: true));
            foreach (var entity in level.Entities)
            {
                if (entity.IsValid)
                    AddEntity(nodes, entity, level.Id);
            }
        }

        EditorExplorerState.Publish(nodes, selection?.Id);
    }

    private static void AddEntity(List<EditorExplorerState.TreeNode> nodes, Entity entity, Guid parentId)
    {
        var transform = entity.GetComponent<TransformComponent>();
        // The entity is already rendered under its transform parent's row; only
        // roots of the transform hierarchy are listed under the level/world.
        if (transform?.HasParent == true)
            return;
        AddEntityRow(nodes, entity, transform, parentId);
    }

    private static void AddEntityRow(List<EditorExplorerState.TreeNode> nodes, Entity entity,
        TransformComponent? transform, Guid parentId)
    {
        nodes.Add(new EditorExplorerState.TreeNode(entity.Name, entity.Id, parentId, IconFor(entity), IsFolder: false));
        if (transform is null)
            return;
        foreach (var child in transform.Children)
        {
            if (child.Entity is { IsValid: true } childEntity)
                AddEntityRow(nodes, childEntity, child, entity.Id);
        }
    }

    private static string IconFor(Entity entity)
    {
        // Each component declares its own editor icon through
        // ComponentIconAttribute; the entity shows the first declared one
        // (the common case: a single spatial component like Camera or
        // MeshRenderer).
        foreach (var component in entity.Components)
        {
            if (component.GetType().GetCustomAttribute<ComponentIconAttribute>(inherit: true) is { } icon)
                return icon.Path;
        }

        return ComponentIconAttribute.DefaultPath;
    }
}
