using Crowbar.Engine.InputSystem;

namespace Crowbar.Editor;

/// <summary>
/// The editor's global keyboard shortcuts: Ctrl+S saves the level, Ctrl+Z
/// undoes, Ctrl+Shift+Z (and Ctrl+Y) redo, Ctrl+Shift+N toggles the notification
/// window. Registered once at startup and fired by <see cref="Update"/> (called
/// each frame after the input poll, so the press edges are fresh). Shortcuts
/// never fire while a text field owns the keyboard — the UI consumes it then, so
/// a field's own editing is never hijacked. The keys are resolved by the
/// character they produce in the active layout (like the camera's ZQSD
/// bindings): Ctrl+Z always means the key labeled Z, whether the layout is
/// AZERTY or QWERTY.
/// </summary>
public static class EditorShortcuts
{
    /// <summary>Registers the editor shortcuts (called once at startup).</summary>
    public static void Register()
    {
        var input = EditorHost.Current!.InputSource;
        ShortcutManager.Instance.IsEnabled = () => !Game.Ui.KeyboardConsumed;

        var undoKey = input.KeyForChar('z');
        var redoKey = input.KeyForChar('y');

        ShortcutManager.Instance.Register(new KeyChord(Key.S, KeyModifiers.Control), Game.SaveLevel);
        ShortcutManager.Instance.Register(new KeyChord(input.KeyForChar('n'), KeyModifiers.Control | KeyModifiers.Shift), NotificationWindow.Toggle);
        ShortcutManager.Instance.Register(new KeyChord(undoKey, KeyModifiers.Control), Game.Undo);
        ShortcutManager.Instance.Register(new KeyChord(undoKey, KeyModifiers.Control | KeyModifiers.Shift), Game.Redo);
        ShortcutManager.Instance.Register(new KeyChord(redoKey, KeyModifiers.Control), Game.Redo);
    }

    /// <summary>Fires the shortcuts whose chord was pressed this frame.</summary>
    public static void Update() => ShortcutManager.Instance.Update();
}
