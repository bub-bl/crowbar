using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

// Renders the editor page through CreateEditorUi: shares the process-global
// editor state, so it must serialize with the EditorPage collection.
[Collection("EditorPage")]

/// <summary>
/// The viewport toolbar writes the requested gizmo tool through the static
/// <see cref="GizmoToolState"/> bridge, which the editor host reads each
/// frame. These tests pin the UI half of that contract against the real
/// editor layout.
/// </summary>
public class ViewportToolbarTests
{
    [Fact]
    public void ToolbarButtonSwitchesTheGizmoMode()
    {
        var previous = GizmoToolState.Mode;
        try
        {
            GizmoToolState.Mode = 0;
            using var ui = EditorPageCompositionTests.CreateEditorUi();
            var content = ui.Content!;

            // The rotate icon also appears in the top bar; scope to the button
            // inside the viewport toolbar.
            var rotateButton = TestUi.FindAll(content, p => p.Classes.Contains("vt-btn"))
                .First(button => TestUi.Find(button, c => c is Icon i && i.Name == "Solar/arrows/Bold/restart") is not null);
            Assert.False(rotateButton.Classes.Contains("vt-btn-active"));

            ui.ProcessPointerDown(rotateButton.Layout.X + 1, rotateButton.Layout.Y + 1);
            ui.ProcessPointerUp(rotateButton.Layout.X + 1, rotateButton.Layout.Y + 1);
            ui.Update();
            ui.Prepare();

            Assert.Equal(1, GizmoToolState.Mode);

            // The active highlight re-rendered onto the rotate button.
            var active = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("vt-btn-active"));
            var activeButton = Assert.Single(active);
            Assert.Contains("vt-btn", activeButton.Classes);
        }
        finally
        {
            GizmoToolState.Mode = previous;
        }
    }
}
