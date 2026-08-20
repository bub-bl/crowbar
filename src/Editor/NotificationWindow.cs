using Crowbar.Engine;
using Crowbar.Engine.Platform;
using Crowbar.Engine.Rendering;

namespace Crowbar.Editor;

/// <summary>
/// The notification popup: a borderless window pinned to the bottom-left of the
/// primary display, created once and never destroyed. Owned by the
/// <see cref="Editor"/> instance. It is not a real window — no chrome, no
/// header, no feed — it exists only to display the single latest compilation
/// notification (success/error). It starts hidden: a compilation result (script
/// hot reload success/error) shows it with that one notification, and toggling
/// or closing it hides it completely (<see cref="NotificationWindowSession"/>).
/// </summary>
public sealed class NotificationWindow
{
    private readonly Editor _editor;
    private WindowSession? _session;

    public NotificationWindow(Editor editor) => _editor = editor;

    /// <summary>Creates (once), hides and positions the persistent notification popup.</summary>
    public void EnsureCreated()
    {
        if (_session is not null)
            return;
        var session = _editor.OpenWindow(new WindowOptions(
            Title: "Crowbar — Notifications",
            Width: 480,
            Height: 110,
            Resizable: false,
            SkipTaskbar: true,
            Borderless: true,
            Visible: false));
        PositionBottomLeft(session.Window);
        _session = session;
        Log.Info("[Window] Notification popup created (bottom-left, hidden).");
    }

    /// <summary>Shows the popup with the latest notification, without stealing the editor's focus.</summary>
    public void Show()
    {
        EnsureCreated();
        if (_session!.Window.IsVisible)
            return;
        _session.Window.SetVisible(true);
        // The popup must not steal the editor's keystrokes: give the focus back.
        _editor.Window.SetInputFocus();
        Log.Info("[Window] Notification popup shown.");
    }

    /// <summary>Shows or completely hides the notification popup.</summary>
    public void Toggle()
    {
        EnsureCreated();
        var visible = !_session!.Window.IsVisible;
        _session.Window.SetVisible(visible);
        if (visible)
            _editor.Window.SetInputFocus();
        Log.Info($"[Window] Notification popup {(visible ? "shown" : "hidden")}.");
    }

    /// <summary>Pins a window to the bottom-left of the primary display, with a margin.</summary>
    private void PositionBottomLeft(IWindow window)
    {
        if (!_editor.Platform.TryGetDisplayBounds(0, out var x, out var y, out var width, out var height))
            return; // unknown display: keep the centered default position
        const int margin = 16;
        window.SetPosition(x + margin, y + height - window.Height - margin);
    }
}

/// <summary>
/// The notification popup's session: its own Razor UI runtime (the
/// single-notification page) and its own WebGPU surface over the shared device.
/// Created once, hidden, and never destroyed: closing it hides it completely, so
/// the session stays alive for the whole application.
/// </summary>
internal sealed class NotificationWindowSession : WindowSession
{
    public NotificationWindowSession(IWindow window, IGraphicsDevice? graphics)
        : base(window, graphics)
    {
    }

    protected override void OnInitialize()
    {
        Editor.ConfigureUiForWindow(Ui, "/notifications");
        base.OnInitialize();
    }
}
