using Crowbar.UI;
using Xunit;

namespace Crowbar.UI.Tests.Input;

/// <summary>
/// <see cref="UiSystem.EnterModal"/> freezes the UI for the duration of a
/// scene interaction (mouse-look orbit, gizmo drag, box selection): hover
/// states, tooltips, cursor resolution, presses, clicks and wheel are all
/// ignored while a session is open, and the first session clears every
/// transient state so nothing lingers when the pointer returns. Sessions are
/// reference-counted, so overlapping interactions release the pointer only
/// once they have all ended.
/// </summary>
public class PointerSuppressionTests
{
    private static Button CreateButton()
    {
        var button = new Button("Click");
        button.SetInlineStyle("width", "80px");
        button.SetInlineStyle("height", "32px");
        return button;
    }

    [Fact]
    public void SuppressedUiIgnoresHoverPressClickAndWheel()
    {
        using var ui = TestUi.Create();
        var button = CreateButton();
        var clicked = 0;
        button.Clicked += _ => clicked++;
        var wheeled = 0;
        ui.PointerWheelChanged += (_, _, _) => wheeled++;
        ui.Screen.AddChild(button);
        ui.Prepare();
        var bx = button.Layout.X + 1;
        var by = button.Layout.Y + 1;

        // Reference hover: outside a modal session, the pointer works.
        ui.ProcessPointerMove(bx, by);
        Assert.True(button.IsHovered);

        // Opening a modal session freezes the UI and resets the state;
        // afterwards no event gets through: no hover, no click, no wheel.
        var modal = ui.EnterModal();
        Assert.False(button.IsHovered);

        ui.ProcessPointerMove(bx, by);
        Assert.False(button.IsHovered);
        Assert.Equal("auto", ui.HoveredCursor);

        ui.ProcessPointerDown(bx, by);
        Assert.False(ui.PointerPressConsumed);
        Assert.False(button.IsPressed);
        Assert.Equal(0, clicked);

        ui.ProcessPointerUp(bx, by);
        Assert.Equal(0, clicked);

        ui.ProcessPointerWheel(bx, by, 0, -10);
        Assert.Equal(0, wheeled);

        // Closing the session: the pointer comes back to life.
        modal.Dispose();
        ui.ProcessPointerMove(bx, by);
        Assert.True(button.IsHovered);
        ui.ProcessPointerDown(bx, by);
        Assert.True(ui.PointerPressConsumed);
        Assert.Equal(1, clicked);
        ui.ProcessPointerUp(bx, by);
        ui.ProcessPointerWheel(bx, by, 0, -10);
        Assert.Equal(1, wheeled);
    }

    [Fact]
    public void ModalSessionsAreReferenceCountedAndIdempotentToDispose()
    {
        using var ui = TestUi.Create();
        Assert.False(ui.PointerInputSuppressed);

        var outer = ui.EnterModal();
        var inner = ui.EnterModal();
        Assert.True(ui.PointerInputSuppressed);

        // Closing the inner session does not release the pointer: the outer
        // one stays open.
        inner.Dispose();
        Assert.True(ui.PointerInputSuppressed);

        // A double dispose has no effect: each handle releases only once.
        outer.Dispose();
        outer.Dispose();
        Assert.False(ui.PointerInputSuppressed);
    }

    [Fact]
    public void ResetPointerStateClearsHoverPressedTooltipAndCursor()
    {
        using var ui = TestUi.Create();
        var button = CreateButton();
        button.SetInlineStyle("cursor", "pointer");
        button.Tooltip = "Hello";
        ui.Screen.AddChild(button);
        ui.Prepare();

        ui.ProcessPointerDown(button.Layout.X + 1, button.Layout.Y + 1);
        Assert.True(button.IsHovered);
        Assert.True(button.IsPressed);
        Assert.Equal("pointer", ui.HoveredCursor);
        Assert.Equal("Hello", ui.Renderer.TooltipText);

        ui.ResetPointerState();

        Assert.False(button.IsHovered);
        Assert.False(button.IsPressed);
        Assert.Equal("auto", ui.HoveredCursor);
        Assert.Null(ui.Renderer.TooltipText);
        Assert.False(ui.PointerPressConsumed);
    }
}
