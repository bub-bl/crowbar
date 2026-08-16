using Crowbar.Engine.Platform;
using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

// The editor page's Explorer panel reads the process-global
// EditorExplorerState (published by the editor host in the real app). The
// tests that render the page serialize on this collection so their shared
// publishes never interleave.
[Collection("EditorPage")]

/// <summary>
/// Guards the custom title bar's geometry publication: the TopBar must hand the
/// platform valid minimize/maximize/close rects so WM_NCHITTEST reproduces the
/// native caption buttons. The buttons stay mounted (and therefore laid out)
/// even when the custom chrome is unsupported — only their opacity hides them —
/// because a <c>display:none</c> subtree is never measured and would publish
/// empty hit regions after the chrome becomes supported.
/// </summary>
public class WindowChromePublishTests
{
    [Fact]
    public void TopBarPublishesValidButtonRectsWhenCustomChromeIsSupported()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();

        // The host mirrors the real window state every frame; on Windows this
        // flips SupportsCustomChrome from the default false to true after the
        // first frame, so the TopBar must republish measured geometry.
        ui.ChromeState = new WindowChromeState(
            HoveredButton: WindowChromeButton.None,
            IsMaximized: false,
            IsActive: true,
            IsFullscreen: false,
            SupportsCustomChrome: true);
        ui.Update();
        ui.Prepare();

        var chrome = ui.WindowChrome;
        Assert.NotNull(chrome);
        Assert.True(chrome!.TitleBarHeight > 0, $"TitleBarHeight = {chrome.TitleBarHeight}");
        Assert.True(chrome.MinimizeButton.Width > 0 && chrome.MinimizeButton.Height > 0,
            $"MinimizeButton = {chrome.MinimizeButton}");
        Assert.True(chrome.MaximizeButton.Width > 0 && chrome.MaximizeButton.Height > 0,
            $"MaximizeButton = {chrome.MaximizeButton}");
        Assert.True(chrome.CloseButton.Width > 0 && chrome.CloseButton.Height > 0,
            $"CloseButton = {chrome.CloseButton}");

        Assert.Equal(WindowChromeButton.Minimize,
            Win32WindowChrome.HitTestButton(chrome, chrome.MinimizeButton.X + chrome.MinimizeButton.Width / 2,
                chrome.MinimizeButton.Y + chrome.MinimizeButton.Height / 2));
        Assert.Equal(WindowChromeButton.Maximize,
            Win32WindowChrome.HitTestButton(chrome, chrome.MaximizeButton.X + chrome.MaximizeButton.Width / 2,
                chrome.MaximizeButton.Y + chrome.MaximizeButton.Height / 2));
        Assert.Equal(WindowChromeButton.Close,
            Win32WindowChrome.HitTestButton(chrome, chrome.CloseButton.X + chrome.CloseButton.Width / 2,
                chrome.CloseButton.Y + chrome.CloseButton.Height / 2));

        // Adjacent regions are half-open: the shared edge belongs to the next
        // button, while a point at the final right edge is outside the chrome.
        Assert.Equal(WindowChromeButton.Maximize,
            Win32WindowChrome.HitTestButton(chrome, chrome.MaximizeButton.X,
                chrome.MaximizeButton.Y + chrome.MaximizeButton.Height / 2));
        Assert.Equal(WindowChromeButton.None,
            Win32WindowChrome.HitTestButton(chrome, chrome.CloseButton.Right,
                chrome.CloseButton.Y + chrome.CloseButton.Height / 2));
    }
}
