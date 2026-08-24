using Crowbar.UI;

namespace Crowbar.UI.Tests.Input;

public class InputTests
{
    [Fact]
    public void HoverActiveAndClickRouting()
    {
        using var ui = TestUi.Create();
        var button = new Button("Click");
        button.SetInlineStyle("width", "80px");
        button.SetInlineStyle("height", "32px");
        button.AddClass("hover-target");
        var clicked = false;
        button.Clicked += _ => clicked = true;
        ui.Screen.AddChild(button);
        ui.Prepare();

        ui.ProcessPointerDown(button.Layout.X + 1, button.Layout.Y + 1);
        Assert.True(clicked);
        Assert.True(button.IsHovered);
        Assert.True(button.IsPressed);

        ui.ProcessPointerUp(button.Layout.X + 1, button.Layout.Y + 1);
        Assert.False(button.IsPressed);

        ui.ProcessPointerMove(639, 199);
        Assert.False(button.IsHovered);
    }

    [Fact]
    public void HoverStateSurvivesScopedStyleApplication()
    {
        using var ui = TestUi.Create();
        var button = new Button("Hover");
        button.SetInlineStyle("width", "60px");
        button.SetInlineStyle("height", "24px");
        button.AddClass("hoverable");
        ui.Screen.AddChild(button);
        ui.LoadStyles(".hoverable { background-color: #000000; } .hoverable:hover { background-color: #00ff00; }");
        ui.Prepare();

        ui.ProcessPointerMove(button.Layout.X + 1, button.Layout.Y + 1);
        ui.Prepare();
        Assert.Equal(new UiColor(0, 255, 0, 255), button.ComputedStyle.BackgroundColor);

        ui.ProcessPointerMove(639, 199);
        ui.Prepare();
        Assert.Equal(new UiColor(0, 0, 0, 255), button.ComputedStyle.BackgroundColor);
    }

    [Fact]
    public void TextEditingBasics()
    {
        using var ui = TestUi.Create();
        var edit = new TextInput();
        edit.SetInlineStyle("width", "160px");
        edit.SetInlineStyle("height", "32px");
        edit.SetValue("I care");
        ui.Screen.AddChild(edit);
        ui.Prepare();

        ui.ProcessPointerDown(edit.Layout.Right - 1, edit.Layout.Y + 1);
        ui.ProcessKey(0x43, true); // C
        Assert.Equal("I carec", edit.Value);

        ui.ProcessKey(0x10, true); // Shift
        ui.ProcessKey(0x31, true); // 1
        ui.ProcessKey(0x10, false);
        Assert.Equal("I carec!", edit.Value);

        ui.ProcessKey(0x11, true); // Ctrl
        ui.ProcessKey(0x41, true); // A
        ui.ProcessKey(0x11, false);
        Assert.True(edit.HasSelection);
        Assert.Equal(0, edit.SelectionStart);
        Assert.Equal(edit.Value.Length, edit.SelectionEnd);

        ui.ProcessKey(0x08, true); // Backspace
        Assert.Equal(string.Empty, edit.Value);
    }

    [Fact]
    public void WordAndMouseSelection()
    {
        using var ui = TestUi.Create();
        var edit = new TextInput();
        edit.SetInlineStyle("width", "160px");
        edit.SetInlineStyle("height", "32px");
        ui.Screen.AddChild(edit);
        ui.Prepare();

        edit.SetValue("one two three");
        ui.ProcessPointerDown(edit.Layout.Right - 1, edit.Layout.Y + 1);
        ui.ProcessKey(0x11, true);
        ui.ProcessKey(0x08, true); // Ctrl+Backspace
        ui.ProcessKey(0x11, false);
        Assert.Equal("one two ", edit.Value);

        edit.SetValue("select me");
        var font = TextLayout.CreateFont(edit.ComputedStyle);
        var targetX = edit.Layout.X + edit.LayoutPadding.Left + TextLayout.Measure(font, "select", edit.ComputedStyle.LetterSpacing);
        ui.ProcessPointerDown(edit.Layout.X + edit.LayoutPadding.Left + 1, edit.Layout.Y + 1);
        ui.ProcessPointerMove(targetX, edit.Layout.Y + 1);
        ui.ProcessPointerUp(targetX, edit.Layout.Y + 1);
        Assert.True(edit.HasSelection);
    }

    [Fact]
    public void FocusTransfersBetweenInputs()
    {
        using var ui = TestUi.Create();
        var first = new TextInput();
        var second = new TextInput();
        first.SetInlineStyle("width", "100px");
        second.SetInlineStyle("width", "100px");
        first.SetValue("a");
        second.SetValue("b");
        ui.Screen.AddChild(first);
        ui.Screen.AddChild(second);
        ui.Prepare();

        ui.ProcessPointerDown(first.Layout.X + 1, first.Layout.Y + 1);
        Assert.True(first.IsFocused);
        Assert.False(second.IsFocused);

        ui.ProcessPointerDown(second.Layout.X + 1, second.Layout.Y + 1);
        Assert.False(first.IsFocused);
        Assert.True(second.IsFocused);
    }
}
