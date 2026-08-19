namespace Crowbar.Engine.InputSystem;

/// <summary>
/// Registry of global keyboard shortcuts (Ctrl+S, F5, ...), each bound to a
/// <see cref="KeyChord"/> and fired on the chord's press edge — once per press,
/// never while held. The host registers its shortcuts once at startup and calls
/// <see cref="Update"/> once per frame after <see cref="Input.Poll"/>.
///
/// <see cref="IsEnabled"/> gates firing: the editor sets it to
/// <c>() =&gt; !Ui.KeyboardConsumed</c> so a shortcut never fires while the user
/// is typing in a text field (the UI owns the keyboard then).
/// </summary>
public sealed class ShortcutManager
{
    private readonly List<(KeyChord Chord, Action Action)> _shortcuts = [];
    private readonly object _lock = new();

    /// <summary>The process-wide manager used by the editor host.</summary>
    public static ShortcutManager Instance { get; } = new();

    /// <summary>
    /// Optional gate evaluated before any shortcut fires (e.g. while a text
    /// field owns the keyboard). A null gate never blocks.
    /// </summary>
    public Func<bool>? IsEnabled { get; set; }

    /// <summary>Registers an action to fire when <paramref name="chord"/> is pressed.</summary>
    public void Register(KeyChord chord, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_lock)
            _shortcuts.Add((chord, action));
    }

    /// <summary>Removes every action registered for the chord.</summary>
    public void Unregister(KeyChord chord)
    {
        lock (_lock)
            _shortcuts.RemoveAll(shortcut => shortcut.Chord == chord);
    }

    /// <summary>Removes every registered shortcut.</summary>
    public void Clear()
    {
        lock (_lock)
            _shortcuts.Clear();
    }

    /// <summary>
    /// Fires the shortcuts whose chord just transitioned to pressed. Call once
    /// per frame, after <see cref="Input.Poll"/> so the edge is fresh.
    /// </summary>
    public void Update()
    {
        if (IsEnabled?.Invoke() == false)
            return;

        (KeyChord Chord, Action Action)[] snapshot;
        lock (_lock)
            snapshot = [.. _shortcuts];

        foreach (var (chord, action) in snapshot)
        {
            if (Input.WasPressed(chord))
                action();
        }
    }
}
