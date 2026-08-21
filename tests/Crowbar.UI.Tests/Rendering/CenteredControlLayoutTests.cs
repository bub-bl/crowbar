using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

// Renders the editor page through CreateEditorUi: shares the process-global
// editor state, so it must serialize with the EditorPage collection.
[Collection("EditorPage")]
public class CenteredControlLayoutTests
{
    private static void AssertTextCentered(Panel parent, string context)
    {
        var text = parent.Children.Single(c => c.TagName == "text");
        var parentCenterX = parent.Layout.X + parent.Layout.Width / 2f;
        var parentCenterY = parent.Layout.Y + parent.Layout.Height / 2f;
        var textCenterX = text.Layout.X + text.Layout.Width / 2f;
        var textCenterY = text.Layout.Y + text.Layout.Height / 2f;

        Assert.True(MathF.Abs(textCenterX - parentCenterX) <= 1.5f,
            $"{context}: text is not horizontally centered");
        Assert.True(MathF.Abs(textCenterY - parentCenterY) <= 1.5f,
            $"{context}: text is not vertically centered");
    }

    [Fact]
    public void TopBarTabsCenterTheirLabels()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var tabs = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("tab"));

        Assert.Equal(3, tabs.Count);
        foreach (var tab in tabs)
            AssertTextCentered(tab, $"tab {string.Join(" ", TestUi.Texts(tab))}");
    }

    [Fact]
    public void InspectorActionCentersItsLabel()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var content = ui.Content!;
        var addComponent = TestUi.Find(content, p => p.Classes.Contains("add-component"));

        Assert.NotNull(addComponent);
        AssertTextCentered(addComponent!, "add-component");
    }
}
