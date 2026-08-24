using Crowbar.Engine;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// Host-side bridge for the inspector panel: consumes generic UI requests,
/// applies them inside undo windows and republishes the inspector snapshot. It
/// deliberately contains no knowledge of individual component types or their
/// specialized workflows.
/// </summary>
public sealed class InspectorBridge
{
    private readonly Editor _editor;

    public InspectorBridge(Editor editor) => _editor = editor;

    /// <summary>
    /// Consumes and applies pending inspector requests on
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

        // Component additions are handled by the generic component service so
        // this bridge remains independent of the component type hierarchy.
        var addRequests = EditorInspectorState.ConsumeAddComponentRequests();
        if (selected is not null && addRequests.Count > 0)
        {
            using var step = _editor.Level.Step("Add a component");
            InspectorComponentService.AddComponents(selected, addRequests);
        }

        EditorInspectorState.PublishAvailableComponents(InspectorComponentService.GetAttachableTypes(selected));
        InspectorStateBuilder.Publish(selected);
    }
}
