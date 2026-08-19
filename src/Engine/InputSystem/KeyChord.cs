namespace Crowbar.Engine.InputSystem;

/// <summary>
/// The modifier keys of a keyboard shortcut, regardless of which side of the
/// keyboard is used (left or right control both count as <see cref="Control"/>).
/// </summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,

    /// <summary>The OS/window key (Cmd on macOS, Win on Windows).</summary>
    Super = 1 << 3
}

/// <summary>
/// A keyboard shortcut: a main <see cref="Key"/> plus the exact modifier keys
/// that must be held for it to match. Matching is exact — a chord registered as
/// Ctrl+S does not fire for Ctrl+Shift+S — so shortcuts never collide with
/// more qualified variants of themselves.
/// </summary>
public readonly record struct KeyChord(Key Key, KeyModifiers Modifiers = KeyModifiers.None)
{
    /// <summary>Human-readable form (e.g. <c>Ctrl+S</c>) for menus, tooltips and notifications.</summary>
    public override string ToString() =>
        Modifiers == KeyModifiers.None
            ? Key.ToString()
            : $"{Describe(Modifiers)}+{Key}";

    private static string Describe(KeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if ((modifiers & KeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & KeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((modifiers & KeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((modifiers & KeyModifiers.Super) != 0) parts.Add("Super");
        return string.Join("+", parts);
    }
}
