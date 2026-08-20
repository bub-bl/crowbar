using System.Numerics;
using Crowbar.Engine;
using Crowbar.Engine.InputSystem;
using Crowbar.Engine.Rendering;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// The editor's 3D viewport: the gizmo interaction (translate/rotate/scale
/// drags with grid snapping), scene picking/selection on left-click, the
/// explorer selection and inspector edit requests, and the per-frame
/// explorer/inspector republish. Reads the live engine state through
/// <see cref="Game"/>; acts only while the primary window is focused and only
/// inside the docked viewport rectangle.
/// </summary>
public static class Viewport
{
    /// <summary>Makes the translation gizmo snap follow the grid cell size.</summary>
    public static void ConfigureSnap()
    {
        if (Game.Renderer is { } renderer)
            renderer.Gizmos.SnapSize = renderer.Grid.CellSize;
    }

    /// <summary>Selects the entity shown at startup (the main cube, else the first mesh).</summary>
    public static void SelectInitial(Level? level)
    {
        if (level is null)
            return;
        Game.Renderer?.Gizmos.Selection = DemoScene.SelectInitial(level);
    }

    /// <summary>
    /// Wires the viewport to the open level (its selection is re-resolved after
    /// an undo/redo restores the document) and publishes the initial panels.
    /// </summary>
    public static void Initialize()
    {
        Game.Restored += OnRestored;
        Publish();
    }

    /// <summary>
    /// Per-frame viewport phase: applies the explorer selection, inspector edits
    /// and Add Component requests (each inside one undo window), republishes the
    /// explorer/inspector, then drives the gizmos: mode, drags (one undo step
    /// per gesture) and picking.
    /// </summary>
    public static void Update(float delta, int viewportWidth, int viewportHeight)
    {
        var world = Game.World;
        var renderer = Game.Renderer;
        var ui = Game.Ui;

        // Selection requested from the Explorer (UI → host): applied to the
        // viewport gizmos, then the hierarchy is republished so the panel
        // reflects the current state (levels, entities, attachments).
        if (EditorExplorerState.ConsumeRequestedSelection() is { } requestedId &&
            world.FindEntity(requestedId) is { } requested)
            renderer?.Gizmos.Selection = requested;

        // Edits queued by the inspector (UI → host) are written back to the
        // selected entity inside one undo window: the whole batch (a field
        // commit) is a single undoable step. A successful edit marks the level
        // dirty through Level.MarkDirty (inside ApplyEdit).
        var selected = renderer?.Gizmos.Selection;
        var pendingEdits = EditorInspectorState.ConsumeEdits();
        if (selected is not null && pendingEdits.Count > 0)
        {
            using var step = Game.Step("Edit a property");
            foreach (var (key, value) in pendingEdits)
                InspectorStateBuilder.ApplyEdit(selected, key, value);
        }

        // The inspector's Add Component requests are applied inside one undoable
        // step (attaching marks the level dirty), then the inspector is
        // republished so the new component's section appears immediately.
        var addRequests = EditorInspectorState.ConsumeAddComponentRequests();
        if (selected is not null && addRequests.Count > 0)
        {
            using var step = Game.Step("Add a component");
            foreach (var typeName in addRequests)
                TypeLibrary.AddComponent(selected, typeName);
        }
        EditorInspectorState.PublishAvailableComponents(TypeLibrary.AttachableTo(selected));

        ExplorerTreeBuilder.Publish(world, selected);
        InspectorStateBuilder.Publish(selected);

        var host = EditorHost.Current!;
        if (renderer is null)
            return; // headless: no gizmo interaction

        // The gizmo tool chosen in the viewport toolbar is applied every frame:
        // rendering and interaction follow the same mode.
        renderer.Gizmos.Mode = GizmoToolState.Mode switch
        {
            1 => GizmoMode.Rotate,
            2 => GizmoMode.Scale,
            _ => GizmoMode.Translate
        };

        // The 3D scene rectangle: the docked viewport or the whole window before
        // the first layout. Also drives the picking math below.
        var rect = Rect(viewportWidth, viewportHeight);
        var width = Math.Max(1, (int)rect.Width);
        var height = Math.Max(1, (int)rect.Height);
        var mouse = Mouse.Position;
        var localMouse = mouse - new Vector2(rect.X, rect.Y);
        var insideViewport = ContainsPointer(viewportWidth, viewportHeight);

        // Gizmo drags and scene picking only act while the primary window is
        // focused: the input facades follow the focused window, so the mouse
        // coordinates are only meaningful for the focused session. A click
        // consumed by the UI (button, tab, input, scrollbar) must neither start
        // a gizmo drag nor select the scene underneath; a drag already engaged
        // continues even if the cursor moves over the UI.
        var primaryFocused = host.IsFocused;
        var matrices = CameraMatrices.Compute(Game.Camera, width, height);
        if (primaryFocused && (!ui.PointerPressConsumed || renderer.Gizmos.IsDragging))
            renderer.Gizmos.UpdateInteraction(matrices, localMouse, Mouse.IsDown(MouseButton.Left));

        // One undo window per drag gesture: opened when the drag starts (BeginDrag
        // does not write — the first Drag write happens on the next frame, inside
        // the window) and committed on release, so hundreds of intermediate
        // transform writes become a single undoable step.
        var gizmos = renderer.Gizmos;
        if (gizmos.IsDragging)
            Game.BeginDragStep(DragLabel(gizmos.Mode));
        else
            Game.EndDragStep();

        // Selects on left-click only when the click did not start a gizmo drag
        // (otherwise moving the entity would re-select the scene), only inside
        // the viewport, not when the UI consumed the click, and only while the
        // primary window is focused.
        if (primaryFocused && insideViewport && !ui.PointerPressConsumed &&
            Mouse.WasPressed(MouseButton.Left) && !renderer.Gizmos.IsDragging)
            renderer.Gizmos.Selection = renderer.Gizmos.Pick(world, matrices, localMouse);
    }

    /// <summary>Clears the selection and republishes the panels (level reload, project switch).</summary>
    public static void ResetSelection()
    {
        Game.Renderer?.Gizmos.Selection = null;
        ExplorerTreeBuilder.Publish(Game.World, null);
        InspectorStateBuilder.Publish(null);
    }

    /// <summary>The docked scene viewport rectangle, or the whole window before the first layout.</summary>
    public static UiRect Rect(int viewportWidth, int viewportHeight) =>
        Game.Ui.SceneViewport ?? new UiRect(0, 0, viewportWidth, viewportHeight);

    /// <summary>True when the cursor is inside the docked viewport rectangle.</summary>
    public static bool ContainsPointer(int viewportWidth, int viewportHeight)
    {
        var rect = Rect(viewportWidth, viewportHeight);
        var mouse = Mouse.Position;
        return mouse.X >= rect.X && mouse.X <= rect.Right &&
               mouse.Y >= rect.Y && mouse.Y <= rect.Bottom;
    }

    /// <summary>Undo/redo rebuilt the document: re-resolve the selection by its stable id.</summary>
    private static void OnRestored()
    {
        var renderer = Game.Renderer;
        if (renderer?.Gizmos.Selection is not { } selected)
            return;
        renderer.Gizmos.Selection = Game.World.FindEntity(selected.Id);
    }

    private static void Publish()
    {
        var selection = Game.Renderer?.Gizmos.Selection;
        ExplorerTreeBuilder.Publish(Game.World, selection);
        InspectorStateBuilder.Publish(selection);
    }

    /// <summary>The undo step label of a gizmo drag, by tool.</summary>
    private static string DragLabel(GizmoMode mode) => mode switch
    {
        GizmoMode.Rotate => "Rotate",
        GizmoMode.Scale => "Resize",
        _ => "Move"
    };
}
