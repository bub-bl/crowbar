using Crowbar.UI;
using Crowbar.UI.Tests.Rendering;

namespace Crowbar.UI.Tests.Input;

/// <summary>
/// The <c>cursor</c> CSS property is resolved by the UI on pointer move: the
/// deepest hovered panel with an explicit cursor (its own or an ancestor's)
/// wins, and the host applies <see cref="UiSystem.HoveredCursor"/> to the OS
/// cursor. These tests cover the resolution itself.
/// </summary>
public class CursorTests
{
    private static Panel PanelAt(float x, float y, float width, float height)
    {
        var panel = new Panel();
        panel.SetInlineStyle("width", $"{width}px");
        panel.SetInlineStyle("height", $"{height}px");
        panel.SetInlineStyle("position", "absolute");
        panel.SetInlineStyle("left", $"{x}px");
        panel.SetInlineStyle("top", $"{y}px");
        return panel;
    }

    [Fact]
    public void HoveringAStyledPanelShowsItsCursor()
    {
        using var ui = TestUi.Create();
        var panel = PanelAt(20, 20, 100, 50);
        panel.SetInlineStyle("cursor", "pointer");
        ui.Screen.AddChild(panel);
        ui.Prepare();

        ui.ProcessPointerMove(40, 40);
        Assert.Equal("pointer", ui.HoveredCursor);

        // Leaving the UI restores the default.
        ui.ProcessPointerMove(600, 450);
        Assert.Equal("auto", ui.HoveredCursor);
    }

    [Fact]
    public void DeepestHoveredCursorWins()
    {
        using var ui = TestUi.Create();
        var outer = PanelAt(0, 0, 300, 300);
        outer.SetInlineStyle("cursor", "pointer");
        var inner = PanelAt(50, 50, 100, 100);
        inner.SetInlineStyle("cursor", "text");
        outer.AddChild(inner);
        ui.Screen.AddChild(outer);
        ui.Prepare();

        ui.ProcessPointerMove(80, 80); // inside the inner panel -> text
        Assert.Equal("text", ui.HoveredCursor);

        ui.ProcessPointerMove(20, 20); // outer only -> pointer
        Assert.Equal("pointer", ui.HoveredCursor);
    }

    [Fact]
    public void CursorInheritsFromAncestorsWhenNotDeclared()
    {
        using var ui = TestUi.Create();
        var outer = PanelAt(0, 0, 300, 300);
        outer.SetInlineStyle("cursor", "pointer");
        var child = PanelAt(50, 50, 100, 100); // no cursor
        outer.AddChild(child);
        ui.Screen.AddChild(outer);
        ui.Prepare();

        ui.ProcessPointerMove(80, 80);
        Assert.Equal("pointer", ui.HoveredCursor);
    }

    [Fact]
    public void ExplicitDefaultOverridesAncestorPointer()
    {
        using var ui = TestUi.Create();
        var outer = PanelAt(0, 0, 300, 300);
        outer.SetInlineStyle("cursor", "pointer");
        var child = PanelAt(50, 50, 100, 100);
        child.SetInlineStyle("cursor", "default");
        outer.AddChild(child);
        ui.Screen.AddChild(outer);
        ui.Prepare();

        ui.ProcessPointerMove(80, 80);
        Assert.Equal("default", ui.HoveredCursor);
    }

    [Fact]
    public void InvalidCursorValueIsIgnored()
    {
        using var ui = TestUi.Create();
        var panel = PanelAt(20, 20, 100, 50);
        panel.SetInlineStyle("cursor", "bogus");
        ui.Screen.AddChild(panel);
        ui.Prepare();

        ui.ProcessPointerMove(40, 40);
        Assert.Equal("auto", ui.HoveredCursor);
    }

    [Fact]
    public void EditorToolbarButtonsShowPointerCursor()
    {
        using var ui = EditorPageCompositionTests.CreateEditorUi();
        var btn = TestUi.Find(ui.Content!, p => p.Classes.Contains("tb-btn"));
        Assert.NotNull(btn);

        ui.ProcessPointerMove(btn!.Layout.X + btn.Layout.Width / 2, btn.Layout.Y + btn.Layout.Height / 2);
        Assert.Equal("pointer", ui.HoveredCursor);
    }
}
