using Crowbar.UI;
using Crowbar.UI.Tests.Rendering;

namespace Crowbar.UI.Tests.Input;

/// <summary>
/// The engine must not treat right-drags over the editor UI as viewport input:
/// the camera orbit only starts when the press begins inside the scene
/// viewport and the UI did not consume it (toolbar buttons, dock tabs,
/// splitters, inputs). These tests pin the consumption contract the engine
/// gates on (<see cref="UiSystem.PointerPressConsumed"/>,
/// <see cref="UiSystem.KeyboardConsumed"/>,
/// <see cref="UiSystem.WheelConsumed"/>).
/// </summary>
public class ViewportInputGatingTests
{
    [Fact]
    public void RightPressOnViewportContentIsNotConsumed()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var rect = ui.SceneViewport!.Value;

        // Centre du viewport, loin de la toolbar (haut du panneau) : un appui
        // droit ici doit rester de l'input scène (orbite), pas de l'input UI.
        ui.ProcessPointerDown(rect.X + rect.Width / 2, rect.Y + rect.Height / 2, button: 1);

        Assert.False(ui.PointerPressConsumed);
    }

    [Fact]
    public void RightPressOnViewportToolbarIsConsumed()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var btn = TestUi.Find(ui.Content!, p => p.Classes.Contains("vt-btn"));
        Assert.NotNull(btn);

        ui.ProcessPointerDown(btn!.Layout.X + btn.Layout.Width / 2, btn.Layout.Y + btn.Layout.Height / 2, button: 1);

        Assert.True(ui.PointerPressConsumed);
    }

    [Fact]
    public void RightPressOnDockTabIsConsumed()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var tab = TestUi.Find(ui.Content!, p => p.Classes.Contains("dock-tab") &&
            p.Attributes.TryGetValue("data-dock-item", out var id) && id == "explorer");
        Assert.NotNull(tab);

        ui.ProcessPointerDown(tab!.Layout.X + tab.Layout.Width / 2, tab.Layout.Y + tab.Layout.Height / 2, button: 1);

        Assert.True(ui.PointerPressConsumed);
    }

    [Fact]
    public void FocusedTextInputConsumesKeyboard()
    {
        using var ui = TestUi.Create();
        var edit = new TextInput();
        edit.SetInlineStyle("width", "160px");
        edit.SetInlineStyle("height", "32px");
        ui.Screen.AddChild(edit);
        ui.Prepare();

        Assert.False(ui.KeyboardConsumed);

        ui.ProcessPointerDown(edit.Layout.X + 1, edit.Layout.Y + 1);
        Assert.True(ui.KeyboardConsumed);

        // Un focus ailleurs (bouton simple) rend le clavier à l'hôte.
        var button = new Button("Click");
        button.SetInlineStyle("width", "80px");
        button.SetInlineStyle("height", "32px");
        button.SetInlineStyle("position", "absolute");
        button.SetInlineStyle("left", "200px");
        button.SetInlineStyle("top", "0px");
        ui.Screen.AddChild(button);
        ui.Prepare();

        ui.ProcessPointerDown(button.Layout.X + 1, button.Layout.Y + 1);
        Assert.False(ui.KeyboardConsumed);
    }

    [Fact]
    public void WheelOverViewportContentIsNotConsumed()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var rect = ui.SceneViewport!.Value;

        // Centre du viewport, loin de la toolbar : la molette ici appartient à
        // la scène (zoom caméra), pas à l'UI.
        ui.ProcessPointerWheel(rect.X + rect.Width / 2, rect.Y + rect.Height / 2, 0, -1);

        Assert.False(ui.WheelConsumed);
    }

    [Fact]
    public void WheelOverViewportToolbarIsConsumed()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var btn = TestUi.Find(ui.Content!, p => p.Classes.Contains("vt-btn"));
        Assert.NotNull(btn);

        ui.ProcessPointerWheel(btn!.Layout.X + btn.Layout.Width / 2, btn.Layout.Y + btn.Layout.Height / 2, 0, -1);

        Assert.True(ui.WheelConsumed);
    }
}
