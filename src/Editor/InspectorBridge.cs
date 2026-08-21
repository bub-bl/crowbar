using Crowbar.Engine;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// Host-side bridge for the inspector panel: consumes UI requests (property
/// edits, add-component) and applies them to the selected entity inside undo
/// windows, then republishes the inspector's available-component list and the
/// panel itself. Runs every frame, separate from the <see cref="Viewport"/>
/// (which handles rendering, gizmo interaction and scene picking).
/// </summary>
public sealed class InspectorBridge
{
    private readonly Editor _editor;

    public InspectorBridge(Editor editor) => _editor = editor;

    /// <summary>
    /// Consumes and applies any pending inspector requests on
    /// <paramref name="selected"/> and republishes the inspector panel.
    /// </summary>
    public void Update(Entity? selected)
    {
        // Property edits queued by the inspector (UI → host): applied inside one
        // undo window (a single field commit = one undoable step). A successful
        // edit marks the level dirty through Level.MarkDirty (inside ApplyEdit).
        var pendingEdits = EditorInspectorState.ConsumeEdits();
        if (selected is not null && pendingEdits.Count > 0)
        {
            using var step = _editor.Level.Step("Edit a property");
            foreach (var (key, value) in pendingEdits)
                InspectorStateBuilder.ApplyEdit(selected, key, value);
        }

        // Add Component requests: applied inside one undoable step, then the
        // available-component list is republished so the menu reflects the
        // updated entity.
        var addRequests = EditorInspectorState.ConsumeAddComponentRequests();
        if (selected is not null && addRequests.Count > 0)
        {
            using var step = _editor.Level.Step("Add a component");
            foreach (var typeName in addRequests)
                AddComponent(selected, typeName);
        }
        EditorInspectorState.PublishAvailableComponents(AttachableTo(selected));

        InspectorStateBuilder.Publish(selected);
    }

    /// <summary>
    /// The component types the entity can still attach: every type registered in
    /// <see cref="GlobalNamespaces.TypeLibrary"/> minus the ones already on it,
    /// ordered by name. The inspector's Add Component menu offers them.
    /// </summary>
    private static IReadOnlyList<string> AttachableTo(Entity? entity)
    {
        if (entity is null)
            return [];
        return TypeLibrary.All
            .Where(type => entity.GetComponent(type) is null)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .Select(type => type.Name)
            .ToArray();
    }

    /// <summary>
    /// Attaches a component by name to the entity. A malformed name, an
    /// already-present type or a throwing constructor is ignored (with a
    /// warning).
    /// </summary>
    private void AddComponent(Entity? entity, string typeName)
    {
        if (entity is null || string.IsNullOrEmpty(typeName))
            return;
        var type = TypeLibrary.Resolve(typeName);
        if (type is null || entity.GetComponent(type) is not null)
            return;
        try
        {
            if (Activator.CreateInstance(type) is Component component)
                entity.AddComponent(component);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Inspector] Failed to add component '{typeName}': {ex.Message}");
        }
    }
}
