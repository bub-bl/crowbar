using System.Diagnostics;
using Crowbar.Engine;
using Crowbar.Engine.Platform;
using Crowbar.Engine.Rendering;
using Crowbar.FileSystems;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// The editor API — the s&amp;box-style <c>Game</c>. Exposes the live engine state
/// of the primary window (world, renderer, camera, UI, window) and the open
/// level's operations: load, save, undo/redo, dirty tracking and the undo
/// "no miss" safety net. Backed by <see cref="EditorHost.Current"/> (no
/// dependency injection); the viewport interaction lives in <see cref="Viewport"/>
/// and the game project lifecycle in <see cref="GameProject"/>.
/// </summary>
public static class Game
{
    private static Level? _level;
    private static IDisposable? _dragStep;
    private static int _lastUnbracketedMutations;

    // Live engine state (the primary window's session) --------------------------

    /// <summary>The world every window renders (assigned by the host).</summary>
    public static World World => EditorHost.Current!.World!;

    /// <summary>The primary window's renderer (scene + Razor composite), or null headless.</summary>
    public static Renderer? Renderer => EditorHost.Current!.HostRenderer;

    /// <summary>The primary window's viewport camera.</summary>
    public static Camera Camera => EditorHost.Current!.Camera;

    /// <summary>The primary window's Razor UI runtime.</summary>
    public static UiSystem Ui => EditorHost.Current!.Ui;

    /// <summary>The primary window.</summary>
    public static IWindow Window => EditorHost.Current!.Window;

    // Open level ------------------------------------------------------------------

    /// <summary>The open level, or null before <see cref="LoadLevel"/> ran.</summary>
    public static Level? Level => _level;

    /// <summary>Raised after the open level was restored by an undo/redo; the viewport re-resolves its selection.</summary>
    public static event Action? Restored;

    /// <summary>
    /// Loads the project's saved level (see <see cref="Project.LevelFileName"/>)
    /// or, when missing or unreadable, builds the <see cref="DemoScene"/>. The
    /// open level starts clean: the title bar's "●" appears only once a
    /// mutation marks it dirty.
    /// </summary>
    public static Level LoadLevel()
    {
        _level = LoadOrCreateLevel();
        _level.ClearDirty();
        return _level;
    }

    /// <summary>Destroys the open level and loads the project's again (project switch).</summary>
    public static void ReloadLevel()
    {
        EndDragStep();
        if (_level is { } old)
        {
            World.DestroyLevel(old);
            _level = null;
        }

        _level = LoadOrCreateLevel();
        _level.ClearDirty();
        EditorUndoState.Publish(false, false, null, null);
        _lastUnbracketedMutations = 0;
    }

    /// <summary>Saves the open level (Ctrl+S); failures surface an error notification and keep it dirty.</summary>
    public static void SaveLevel()
    {
        if (_level is not { IsValid: true })
            return;

        var fileName = Project.LevelFileName;
        try
        {
            LevelFile.Save(_level, fileName);
            _level.ClearDirty();
            Log.Info($"[Level] Saved: {fileName}");
            UiNotifications.Show("Level", $"Level saved: {fileName}", "success");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Level] Save failed: {ex}");
            UiNotifications.Show("Level", $"Save failed: {ex.Message}", "error");
        }
    }

    /// <summary>Undoes the last edit of the open level (Ctrl+Z or the toolbar button).</summary>
    public static void Undo()
    {
        _level?.History.Undo();
        // Undo mid-drag commits the drag window itself: drop the handle so the
        // rest of the gesture opens a fresh window (the disposed one is a no-op
        // anyway, but it would block a new window for the next frame).
        _dragStep = null;
        Restored?.Invoke();
    }

    /// <summary>Redoes the last undone edit (Ctrl+Shift+Z, Ctrl+Y or the toolbar button).</summary>
    public static void Redo()
    {
        _level?.History.Redo();
        _dragStep = null;
        Restored?.Invoke();
    }

    /// <summary>Opens one undo window for an edit batch (a field commit is a single undoable step).</summary>
    public static IDisposable Step(string label) => _level!.History.Step(label);

    /// <summary>Opens the undo window of a gizmo drag gesture, once per gesture.</summary>
    public static void BeginDragStep(string label)
    {
        if (_dragStep is null)
            _dragStep = _level!.History.Step(label);
    }

    /// <summary>Commits the drag gesture's undo window (the drag was released).</summary>
    public static void EndDragStep()
    {
        _dragStep?.Dispose();
        _dragStep = null;
    }

    /// <summary>Consumes the undo/redo requests queued by the toolbar buttons.</summary>
    public static void UpdateRequests()
    {
        if (_level is null)
            return;
        if (EditorUndoState.ConsumeUndoRequest())
            Undo();
        if (EditorUndoState.ConsumeRedoRequest())
            Redo();
    }

    /// <summary>Publishes the open level's name and dirty flag for the title bar.</summary>
    public static void PublishLevelState() =>
        EditorDocumentState.Publish(_level?.Name ?? string.Empty, _level?.IsDirty ?? false);

    /// <summary>Publishes the undo/redo capability of the open level for the toolbar.</summary>
    public static void PublishUndoState()
    {
        if (_level is not { } document)
        {
            EditorUndoState.Publish(false, false, null, null);
            return;
        }

        var history = document.History;
        EditorUndoState.Publish(history.CanUndo, history.CanRedo, history.UndoLabel, history.RedoLabel);
    }

    /// <summary>
    /// The undo "no miss" safety net: asserts when a level mutation was observed
    /// outside any undo window since the last frame. Runtime script mutations
    /// during play are expected and excluded; in edit mode an unbracketed
    /// mutation is a missed <c>Step()</c> and fails loudly instead of silently
    /// corrupting undo.
    /// </summary>
    public static void DetectUnbracketedMutations()
    {
        // A disposed level (world teardown) can no longer receive user edits.
        if (_level is not { IsValid: true } document)
            return;

        var history = document.History;
        if (history.UnbracketedMutationCount == _lastUnbracketedMutations)
            return;
        _lastUnbracketedMutations = history.UnbracketedMutationCount;

        if (World.IsPlaying)
            return;
        Debug.Assert(false, "[Undo] A document mutation escaped its undo window — wrap the action in level.History.Step().");
        UiNotifications.Show("Undo", "Mutation outside an undo window — add a Step().", "error");
    }

    private static Level LoadOrCreateLevel()
    {
        var fileName = Project.LevelFileName;
        if (FileSystem.Project.FileExists(fileName))
        {
            try
            {
                var file = LevelFile.Load(fileName);
                var level = file.CreateLevel(World);
                Log.Info($"[Level] Loaded '{fileName}': {level.Entities.Count} entit(ies).");
                return level;
            }
            catch (Exception ex)
            {
                Log.Warn($"[Level] Failed to load '{fileName}': {ex.Message} — building the demo scene.");
                UiNotifications.Show("Level", $"Level '{fileName}' unreadable — demo scene loaded", "error");
            }
        }

        return DemoScene.Build(World);
    }
}
