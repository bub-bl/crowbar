using System.Diagnostics;
using Crowbar.Engine;
using Crowbar.FileSystems;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// The open level — an editor tool owned by the <see cref="Editor"/> instance.
/// Owns the document lifecycle: load and save (by file name, from
/// <see cref="Project.LevelFileName"/>), undo/redo windows, dirty tracking and
/// the undo "no miss" safety net. The shared <see cref="GlobalNamespaces.Game"/>
/// API only exposes the live session state (world, renderer, camera, UI,
/// window); everything about the open level is editor-side, so a game project
/// never sees it.
/// </summary>
public sealed class EditorLevel
{
    private Level? _level;
    private IDisposable? _dragStep;
    private int _lastUnbracketedMutations;

    /// <summary>The open level, or null before <see cref="Load"/> ran.</summary>
    public Level? Level => _level;

    /// <summary>Raised after the open level was restored by an undo/redo; the viewport re-resolves its selection.</summary>
    public event Action? Restored;

    /// <summary>
    /// Loads the project's level file (<paramref name="fileName"/>) or, when
    /// missing or unreadable, builds the <see cref="DemoScene"/>. The open level
    /// starts clean: the title bar's "●" appears only once a mutation marks it
    /// dirty.
    /// </summary>
    public Level Load(string fileName)
    {
        _level = LoadOrCreateLevel(fileName);
        _level.ClearDirty();
        return _level;
    }

    /// <summary>Destroys the open level and loads the project's again (project switch).</summary>
    public void Reload(string fileName)
    {
        EndDragStep();
        if (_level is { } old)
        {
            Game.World.DestroyLevel(old);
            _level = null;
        }

        _level = LoadOrCreateLevel(fileName);
        _level.ClearDirty();
        EditorUndoState.Publish(false, false, null, null);
        _lastUnbracketedMutations = 0;
    }

    /// <summary>Saves the open level to <paramref name="fileName"/> (Ctrl+S); failures surface an error notification and keep it dirty.</summary>
    public void Save(string fileName)
    {
        if (_level is not { IsValid: true })
            return;

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
    public void Undo()
    {
        _level?.History.Undo();
        // Undo mid-drag commits the drag window itself: drop the handle so the
        // rest of the gesture opens a fresh window (the disposed one is a no-op
        // anyway, but it would block a new window for the next frame).
        _dragStep = null;
        Restored?.Invoke();
    }

    /// <summary>Redoes the last undone edit (Ctrl+Shift+Z, Ctrl+Y or the toolbar button).</summary>
    public void Redo()
    {
        _level?.History.Redo();
        _dragStep = null;
        Restored?.Invoke();
    }

    /// <summary>Opens one undo window for an edit batch (a field commit is a single undoable step).</summary>
    public IDisposable Step(string label) => _level!.History.Step(label);

    /// <summary>Opens the undo window of a gizmo drag gesture, once per gesture.</summary>
    public void BeginDragStep(string label)
    {
        if (_dragStep is null)
            _dragStep = _level!.History.Step(label);
    }

    /// <summary>Commits the drag gesture's undo window (the drag was released).</summary>
    public void EndDragStep()
    {
        _dragStep?.Dispose();
        _dragStep = null;
    }

    /// <summary>Consumes the undo/redo requests queued by the toolbar buttons.</summary>
    public void UpdateRequests()
    {
        if (_level is null)
            return;
        if (EditorUndoState.ConsumeUndoRequest())
            Undo();
        if (EditorUndoState.ConsumeRedoRequest())
            Redo();
    }

    /// <summary>Publishes the open level's name and dirty flag for the title bar.</summary>
    public void PublishLevelState() =>
        EditorDocumentState.Publish(_level?.Name ?? string.Empty, _level?.IsDirty ?? false);

    /// <summary>Publishes the undo/redo capability of the open level for the toolbar.</summary>
    public void PublishUndoState()
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
    public void DetectUnbracketedMutations()
    {
        // A disposed level (world teardown) can no longer receive user edits.
        if (_level is not { IsValid: true } document)
            return;

        var history = document.History;
        if (history.UnbracketedMutationCount == _lastUnbracketedMutations)
            return;
        _lastUnbracketedMutations = history.UnbracketedMutationCount;

        if (Game.World.IsPlaying)
            return;
        Debug.Assert(false, "[Undo] A document mutation escaped its undo window — wrap the action in level.History.Step().");
        UiNotifications.Show("Undo", "Mutation outside an undo window — add a Step().", "error");
    }

    private Level LoadOrCreateLevel(string fileName)
    {
        if (FileSystem.Project.FileExists(fileName))
        {
            try
            {
                var file = LevelFile.Load(fileName);
                var level = file.CreateLevel(Game.World);
                Log.Info($"[Level] Loaded '{fileName}': {level.Entities.Count} entit(ies).");
                return level;
            }
            catch (Exception ex)
            {
                Log.Warn($"[Level] Failed to load '{fileName}': {ex.Message} — building the demo scene.");
                UiNotifications.Show("Level", $"Level '{fileName}' unreadable — demo scene loaded", "error");
            }
        }

        return DemoScene.Build(Game.World);
    }
}
