using Crowbar.UI;
using Xunit;

namespace Crowbar.UI.Tests.Input;

/// <summary>
/// <see cref="UiSystem.PointerInputSuppressed"/> freezes the UI for the
/// duration of a scene interaction (mouse-look orbit): hover states, tooltips,
/// cursor resolution, presses, clicks and wheel are all ignored, and
/// <see cref="UiSystem.ResetPointerState"/> clears every transient state so
/// nothing lingers when the pointer returns.
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

        // Survol de référence : hors suppression, le pointeur fonctionne.
        ui.ProcessPointerMove(bx, by);
        Assert.True(button.IsHovered);

        // Suppression : l'UI est gelée pour la durée de la session. Le host
        // appelle ResetPointerState au démarrage, ce qui remet l'état à zéro ;
        // ensuite aucun événement ne traverse, ni survol, ni clic, ni molette.
        ui.PointerInputSuppressed = true;
        ui.ResetPointerState();
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

        // Réactivation : le pointeur revient à la vie.
        ui.PointerInputSuppressed = false;
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
    public void ResetPointerStateClearsHoverPressedTooltipAndCursor()
    {
        using var ui = TestUi.Create();
        var button = CreateButton();
        button.SetInlineStyle("cursor", "pointer");
        button.Tooltip = "Bonjour";
        ui.Screen.AddChild(button);
        ui.Prepare();

        ui.ProcessPointerDown(button.Layout.X + 1, button.Layout.Y + 1);
        Assert.True(button.IsHovered);
        Assert.True(button.IsPressed);
        Assert.Equal("pointer", ui.HoveredCursor);
        Assert.Equal("Bonjour", ui.Renderer.TooltipText);

        ui.ResetPointerState();

        Assert.False(button.IsHovered);
        Assert.False(button.IsPressed);
        Assert.Equal("auto", ui.HoveredCursor);
        Assert.Null(ui.Renderer.TooltipText);
        Assert.False(ui.PointerPressConsumed);
    }
}
