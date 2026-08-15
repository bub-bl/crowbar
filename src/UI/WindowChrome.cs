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
/// UI → host channel for the live window-chrome state the platform cannot see
/// from the UI alone (the hovered native caption button and the maximized
/// state, both resolved on the OS side). The host writes it every frame and the
/// TopBar reads it through its BuildHash to re-render the button feedback.
/// </summary>
public static class WindowChromeState
{
    /// <summary>The caption button the cursor is hovering, or <see cref="WindowChromeButton.None"/>.</summary>
    public static WindowChromeButton HoveredButton;

    /// <summary>True while the window is maximized (the top bar shows the restore glyph).</summary>
    public static bool IsMaximized;
}
