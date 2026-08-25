using Crowbar.Engine.InputSystem;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// The editor's global keyboard shortcuts: Ctrl+S saves the level, Ctrl+Z
/// undoes, Ctrl+Shift+Z (and Ctrl+Y) redo, Ctrl+Shift+N toggles the notification
/// window, and Delete removes the file or folder selected in the Content drawer.
/// Owned by the <see cref="Editor"/> instance. Registered once at
/// startup and fired by <see cref="Update"/> (called each frame after the input
/// poll, so the press edges are fresh). Shortcuts never fire while a text field
/// owns the keyboard — the UI consumes it then, so a field's own editing is
/// never hijacked. The keys are resolved by the character they produce in the
/// active layout (like the camera's ZQSD bindings): Ctrl+Z always means the key
/// labeled Z, whether the layout is AZERTY or QWERTY.
/// </summary>
public sealed class Shortcuts
{
    private readonly Editor _editor;
    private readonly NotificationWindow _notificationWindow;

    public Shortcuts(Editor editor, NotificationWindow notificationWindow)
    {
        _editor = editor;
        _notificationWindow = notificationWindow;
    }

    /// <summary>Registers the editor shortcuts (called once at startup).</summary>
    public void Register()
    {
        var input = _editor.InputSource;
        ShortcutManager.Instance.IsEnabled = () => !Game.Ui.KeyboardConsumed;

        var undoKey = input.KeyForChar('z');
        var redoKey = input.KeyForChar('y');

        ShortcutManager.Instance.Register(new KeyChord(Key.S, KeyModifiers.Control), _editor.SaveLevel);
        ShortcutManager.Instance.Register(new KeyChord(input.KeyForChar('n'), KeyModifiers.Control | KeyModifiers.Shift), _notificationWindow.Toggle);
        // The physical Delete key (Fn+N on laptops without a dedicated key):
        // deletes whatever the Content drawer currently has selected.
        ShortcutManager.Instance.Register(new KeyChord(Key.Delete), DeleteSelectedContent);
        ShortcutManager.Instance.Register(new KeyChord(undoKey, KeyModifiers.Control), _editor.Level.Undo);
        ShortcutManager.Instance.Register(new KeyChord(undoKey, KeyModifiers.Control | KeyModifiers.Shift), _editor.Level.Redo);
        ShortcutManager.Instance.Register(new KeyChord(redoKey, KeyModifiers.Control), _editor.Level.Redo);

        // Viewport gizmo tool shortcuts (also reachable through the viewport
        // toolbar): W moves, E rotates, R scales, G toggles the ground grid.
        // They are bound by the character they produce (like the camera's ZQSD
        // bindings), so W/E/R/G work on any layout: on AZERTY those letters sit
        // on different physical keys, and a physical-key binding would miss them.
        ShortcutManager.Instance.Register(new KeyChord(input.KeyForChar('w')), SetTool(0));
        ShortcutManager.Instance.Register(new KeyChord(input.KeyForChar('e')), SetTool(1));
        ShortcutManager.Instance.Register(new KeyChord(input.KeyForChar('r')), SetTool(2));
        ShortcutManager.Instance.Register(new KeyChord(input.KeyForChar('g')), ToggleGrid);
    }

    private static Action SetTool(int mode) => () => GizmoToolState.Mode = mode;

    private static void ToggleGrid()
    {
        if (Game.Renderer is { } renderer)
            renderer.Grid.Visible = !renderer.Grid.Visible;
    }

    /// <summary>Fires the shortcuts whose chord was pressed this frame.</summary>
    public void Update() => ShortcutManager.Instance.Update();

    /// <summary>
    /// Deletes the file or folder selected in the Content drawer (Delete / Fn+N
    /// on compact keyboards). The request runs through the same host path as
    /// the context menu's Delete action, which shows a notification on success;
    /// nothing happens without a selection. Deleting is intentionally direct
    /// (no confirmation) — the context menu keeps its explicit confirm flow.
    /// </summary>
    private void DeleteSelectedContent()
    {
        if (EditorContentState.SelectedPath is not { Length: > 0 } path)
            return;
        EditorContentState.RequestAction(EditorContentState.ActionKind.Delete, path);
    }
}
