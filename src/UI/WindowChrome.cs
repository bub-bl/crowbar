namespace Crowbar.UI;

/// <summary>Which native caption button the cursor is currently over (driven by WM_NCHITTEST).</summary>
public enum WindowChromeButton
{
    None = 0,
    Minimize = 1,
    Maximize = 2,
    Close = 3,
}

/// <summary>
/// Geometry of the custom title bar, published by the TopBar in framebuffer
/// (drawable) window coordinates. The platform uses it to answer WM_NCHITTEST:
/// the three caption buttons behave natively (including the Windows 11
/// snap-layout flyout on the maximize button), the <see cref="InteractiveRects"/>
/// stay in the client area so the app receives their clicks, and the rest of the
/// <see cref="TitleBarHeight"/> strip is the draggable caption.
/// </summary>
public sealed record WindowChromeLayout(
    float TitleBarHeight,
    UiRect MinimizeButton,
    UiRect MaximizeButton,
    UiRect CloseButton,
    IReadOnlyList<UiRect> InteractiveRects);

/// <summary>
/// Live window-chrome state mirrored from the platform window into the UI. The
/// host writes it every frame; the TopBar hashes the fields it renders so the
/// caption feedback (hovered button, restore glyph, inactive dimming,
/// fullscreen) updates without a global timer. Unlike a static singleton, this
/// is a plain value: each window carries its own state, so multiple windows and
/// tests never share it.
/// </summary>
public readonly record struct WindowChromeState(
    WindowChromeButton HoveredButton = WindowChromeButton.None,
    bool IsMaximized = false,
    bool IsActive = true,
    bool IsFullscreen = false,
    bool SupportsCustomChrome = false)
{
    /// <summary>Safe fallback before the host has mirrored a real window (active, no custom chrome).</summary>
    public static WindowChromeState Default => new(IsActive: true);
}
