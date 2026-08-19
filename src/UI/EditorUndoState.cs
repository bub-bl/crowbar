namespace Crowbar.UI;

/// <summary>
/// Undo/redo state shown in the editor toolbar and its request channel
/// (UI → host). The host publishes <see cref="CanUndo"/>/<see cref="CanRedo"/>
/// and the step labels every frame; the toolbar buttons request undo/redo
/// through <see cref="RequestUndo"/>/<see cref="RequestRedo"/>, consumed by
/// the host each frame exactly like <see cref="EditorExplorerState.RequestSelection"/>.
///
/// This static only <em>mirrors</em> the document's history for the panels —
/// the history itself lives on the document (<c>Level.History</c>, an instance
/// owned by the host), never here. When the editor becomes multi-window, each
/// window host publishes and consumes its own document's state through this
/// same shape (like the other state bridges); the core
/// <see cref="Crowbar.Engine.Undo.UndoHistory"/> API needs no change.
/// </summary>
public static class EditorUndoState
{
    private static bool _canUndo;
    private static bool _canRedo;
    private static string _undoLabel = string.Empty;
    private static string _redoLabel = string.Empty;
    private static int _version;

    private static readonly Lock RequestLock = new();
    private static bool _undoRequested;
    private static bool _redoRequested;

    /// <summary>True when the open document has a step to undo (toolbar button enabled).</summary>
    public static bool CanUndo => _canUndo;

    /// <summary>True when an undone step can be redone (toolbar button enabled).</summary>
    public static bool CanRedo => _canRedo;

    /// <summary>Label of the step the next undo would revert, or empty.</summary>
    public static string UndoLabel => _undoLabel;

    /// <summary>Label of the step the next redo would replay, or empty.</summary>
    public static string RedoLabel => _redoLabel;

    /// <summary>Bumped whenever the published undo/redo state changes; the toolbar hashes it.</summary>
    public static int Version => _version;

    /// <summary>Queues an undo request (toolbar click) for the host to consume this frame.</summary>
    public static void RequestUndo()
    {
        lock (RequestLock)
            _undoRequested = true;
    }

    /// <summary>Queues a redo request (toolbar click) for the host to consume this frame.</summary>
    public static void RequestRedo()
    {
        lock (RequestLock)
            _redoRequested = true;
    }

    /// <summary>Returns and clears the pending undo request, or false.</summary>
    public static bool ConsumeUndoRequest()
    {
        lock (RequestLock)
        {
            var requested = _undoRequested;
            _undoRequested = false;
            return requested;
        }
    }

    /// <summary>Returns and clears the pending redo request, or false.</summary>
    public static bool ConsumeRedoRequest()
    {
        lock (RequestLock)
        {
            var requested = _redoRequested;
            _redoRequested = false;
            return requested;
        }
    }

    /// <summary>
    /// Replaces the toolbar state; a no-op when nothing changed (the host
    /// publishes every frame, so identical frames must not force a rebuild).
    /// </summary>
    public static void Publish(bool canUndo, bool canRedo, string? undoLabel, string? redoLabel)
    {
        undoLabel ??= string.Empty;
        redoLabel ??= string.Empty;
        if (canUndo == _canUndo && canRedo == _canRedo &&
            string.Equals(undoLabel, _undoLabel, StringComparison.Ordinal) &&
            string.Equals(redoLabel, _redoLabel, StringComparison.Ordinal))
            return;

        _canUndo = canUndo;
        _canRedo = canRedo;
        _undoLabel = undoLabel;
        _redoLabel = redoLabel;
        _version++;
    }
}
