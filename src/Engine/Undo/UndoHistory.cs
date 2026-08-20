namespace Crowbar.Engine.Undo;

/// <summary>
/// Undo/redo history for a single document, built as a <em>tape</em> of
/// full-state snapshots (the memento pattern): the first entry is the state at
/// creation, every committed step appends the document's serialized state, and
/// undo/redo moves a position pointer and restores the state it lands on.
/// A new step after an undo truncates the redo branch; the tape is bounded by
/// <paramref name="maxDepth"/> (the baseline is never trimmed).
///
/// Capture is <em>observation-based</em>, not interception-based: a
/// <see cref="Step"/> window snapshots the document when it opens and again
/// when it closes, and records the change if the two differ. Any mutation that
/// alters the document inside the window is therefore captured — there is no
/// list of mutation sites to keep in sync, so a change can never silently
/// escape the history, including mutations from code paths that do not exist
/// yet. The window is an <see cref="IDisposable"/>, so forgetting to commit
/// never loses data: the dispose <em>is</em> the commit (it only widens the
/// step). The one remaining miss — opening no window around an action — is the
/// job of the host's debug detector, which compares the document's
/// <c>ChangeCount</c> against the open windows.
///
/// The history is deliberately document-agnostic: it only knows how to capture
/// and restore the document through the two delegates passed to the
/// constructor, so any tool document (a level, an animation, a material
/// library, a blueprint…) gets undo/redo with two lines — no engine coupling.
/// It is single-threaded: the editor drives it from its main loop.
/// </summary>
public sealed class UndoHistory
{
    private readonly Func<string> _capture;
    private readonly Action<string> _restore;
    private readonly int _maxDepth;

    private readonly List<(string State, string? Label)> _tape = [];
    private int _position;
    private int _savedPosition;
    private bool _forcedDirty;
    private bool _restoring;
    private readonly List<StepHandle> _windows = [];
    private string? _lastCommittedLabel;

    /// <summary>
    /// Creates a history whose baseline is the document's current state.
    /// <paramref name="capture"/> serializes the document into a snapshot
    /// string; <paramref name="restore"/> rebuilds the document from one.
    /// </summary>
    public UndoHistory(Func<string> capture, Action<string> restore, int maxDepth = 100)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(restore);
        _capture = capture;
        _restore = restore;
        _maxDepth = Math.Max(1, maxDepth);
        _tape.Add((capture(), null));
    }

    /// <summary>True when a step can be undone (a committed step exists before the current position).</summary>
    public bool CanUndo => _position > 0;

    /// <summary>True when an undone step can be redone.</summary>
    public bool CanRedo => _position < _tape.Count - 1;

    /// <summary>
    /// True when the document differs from its last saved state: the position
    /// moved past <see cref="SavedPosition"/>, a mutation was observed outside
    /// any window (runtime scripts), or a step window is currently open. This
    /// is the single source of truth the host feeds the title bar — undoing
    /// back to the saved state clears the dirty flag, undoing away re-sets it.
    /// </summary>
    public bool IsDirty => _forcedDirty || _position != _savedPosition || _windows.Count > 0;

    /// <summary>Index of the state the document currently matches.</summary>
    public int Position => _position;

    /// <summary>Tape index of the last <see cref="MarkSaved"/> (the document as saved).</summary>
    public int SavedPosition => _savedPosition;

    /// <summary>True while a <see cref="Step"/> window is open (a user action is in flight).</summary>
    public bool WindowOpen => _windows.Count > 0;

    /// <summary>
    /// Count of document mutations observed <em>outside</em> any
    /// <see cref="Step"/> window (and outside undo/redo restores). This is the
    /// host's debug detector signal: an action that mutates without a window is
    /// an undo miss, and the host asserts when this counter moves. Because the
    /// count is recorded <em>inside</em> <see cref="NotifyMutation"/>, a window
    /// that opens and closes within the same frame (e.g. an inspector edit) is
    /// still recognized as bracketed — an end-of-frame counter comparison could
    /// not tell the difference.
    /// </summary>
    public int UnbracketedMutationCount { get; private set; }

    /// <summary>Label of the step that undoing would revert, or null when nothing can be undone.</summary>
    public string? UndoLabel => CanUndo ? _tape[_position].Label : null;

    /// <summary>Label of the step that redoing would replay, or null when nothing can be redone.</summary>
    public string? RedoLabel => CanRedo ? _tape[_position + 1].Label : null;

    /// <summary>Label of the most recently committed step (menu display), or null.</summary>
    public string? LastCommittedLabel => _lastCommittedLabel;

    /// <summary>
    /// Opens an undo window: the document is snapshotted now and again when the
    /// returned handle is disposed, and the change (if any) is committed as one
    /// step. Wrap every user action that may mutate the document:
    /// <c>using var step = history.Step("Move");</c>. A window that changes
    /// nothing commits nothing. Windows may nest — each commits its own slice.
    /// </summary>
    public IDisposable Step(string? label = null)
    {
        var handle = new StepHandle(this, _capture(), label);
        _windows.Add(handle);
        return handle;
    }

    /// <summary>
    /// Reverts the document to the previous step. Any open window is committed
    /// first (a drag in progress becomes one step, then that step is undone),
    /// so an undo mid-gesture never loses or misattributes mutations. The
    /// restore itself is not recorded as a new mutation.
    /// </summary>
    public void Undo()
    {
        if (!CanUndo)
            return;
        CommitOpenWindows();
        Move(-1);
    }

    /// <summary>Replays the next undone step. See <see cref="Undo"/> for the window policy.</summary>
    public void Redo()
    {
        if (!CanRedo)
            return;
        CommitOpenWindows();
        Move(+1);
    }

    /// <summary>
    /// Records the current position as the saved state (called by the host
    /// after a successful save) and clears the forced-dirty fallback. The tape
    /// itself is kept: undoing past the save point re-dirties the document.
    /// </summary>
    public void MarkSaved()
    {
        _savedPosition = _position;
        _forcedDirty = false;
    }

    /// <summary>
    /// Re-baselines the history on the document's current state and forgets
    /// every step (called when a new document is loaded or replaced). The
    /// document starts clean and undo/redo start empty.
    /// </summary>
    public void Reset()
    {
        _tape.Clear();
        _tape.Add((_capture(), null));
        _position = 0;
        _savedPosition = 0;
        _forcedDirty = false;
        _lastCommittedLabel = null;
        _windows.Clear();
        UnbracketedMutationCount = 0;
    }

    /// <summary>
    /// Reports an observed document mutation (the document's dirty path calls
    /// this through its level). Outside an undo window this is the forced-dirty
    /// fallback — a mutation nobody bracketed (e.g. runtime scripts) must still
    /// mark the document dirty. Inside a window it is redundant but harmless:
    /// the window commit moves the position anyway. Restores performed by
    /// <see cref="Undo"/>/<see cref="Redo"/> are not new mutations.
    /// </summary>
    public void NotifyMutation()
    {
        if (_restoring)
            return;
        if (_windows.Count == 0)
            UnbracketedMutationCount++;
        _forcedDirty = true;
    }

    private void Move(int delta)
    {
        _restoring = true;
        try
        {
            _position += delta;
            _restore(_tape[_position].State);
        }
        finally
        {
            _restoring = false;
        }
    }

    private void CommitStep(string before, string? label)
    {
        var after = _capture();
        if (string.Equals(after, before, StringComparison.Ordinal))
            return; // no-op window: nothing changed, discard

        // A new step truncates the redo branch, then appends.
        if (_position < _tape.Count - 1)
            _tape.RemoveRange(_position + 1, _tape.Count - _position - 1);
        _tape.Add((after, label));
        _lastCommittedLabel = label;

        // Bounded depth: drop the oldest step (index 1 — the baseline at index
        // 0 survives so undo always has a floor). If the saved state was
        // dropped, forget the save marker: the document must then always
        // appear dirty rather than accidentally clean. The position is reset
        // after trimming so it always points at the last (current) entry.
        while (_tape.Count - 1 > _maxDepth)
        {
            if (_savedPosition == 1)
                _savedPosition = -1;
            else if (_savedPosition > 1)
                _savedPosition--;
            _tape.RemoveAt(1);
        }
        _position = _tape.Count - 1;
    }

    /// <summary>
    /// Closes a step window: removes it from the open set (disposal order does
    /// not matter) and commits its slice. Idempotent — a handle disposed twice
    /// (or after <see cref="Undo"/>/<see cref="Redo"/> already committed it)
    /// is a no-op.
    /// </summary>
    private void CloseWindow(StepHandle handle, string before, string? label)
    {
        _windows.Remove(handle);
        CommitStep(before, label);
    }

    private void CommitOpenWindows()
    {
        // The open windows are committed from the innermost out; a window that
        // changed nothing commits nothing. Disposal is idempotent, so each
        // handle is closed exactly once.
        while (_windows.Count > 0)
            _windows[^1].Dispose();
    }

    private sealed class StepHandle : IDisposable
    {
        private UndoHistory? _owner;
        private readonly string _before;
        private readonly string? _label;

        public StepHandle(UndoHistory owner, string before, string? label)
        {
            _owner = owner;
            _before = before;
            _label = label;
        }

        public void Dispose()
        {
            var owner = _owner;
            if (owner is null)
                return;
            _owner = null;
            owner.CloseWindow(this, _before, _label);
        }
    }
}
