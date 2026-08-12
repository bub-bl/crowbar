using Crowbar.UI;

namespace Crowbar.UI.Tests.Styling;

/// <summary>
/// Guards the reused compute buffer: each panel reuses one ComputedStyle as
/// the cascade's write target (reset to defaults in place, adopted as the
/// visible style on the equal path) instead of allocating a fresh one per
/// pass. These tests assert the three failure modes of that reuse — inherited
/// values accumulating across passes, stale values surviving a buffer reset,
/// and change detection breaking when the visible style aliases the buffer.
/// </summary>
public class ComputedStyleReuseTests
{
    private const int Width = 320;
    private const int Height = 200;

    [Fact]
    public void InheritedOpacityDoesNotAccumulateAcrossPasses()
    {
        // opacity multiplies down the tree during the layout pass, so a buffer
        // that was not reset to defaults before each pass would compound the
        // value (0.5, then 0.25, then 0.125...).
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.LoadStyles(".parent { opacity: 0.5; width: 100px; height: 100px; }");
        var parent = new Panel { TagName = "div" };
        parent.AddClass("parent");
        var child = new Panel { TagName = "div" };
        parent.AddChild(child);
        ui.Screen.AddChild(parent);
        ui.Render();

        Assert.Equal(0.5f, child.ComputedStyle.Opacity, 4);

        // Alternate full re-cascades and style-only re-cascades (the hover
        // toggle re-runs the scoped cascade without changing any rule match,
        // exercising the equal path where the buffer is adopted in place).
        for (var i = 0; i < 5; i++)
        {
            child.SetHovered(i % 2 == 0);
            ui.Render();
            Assert.Equal(0.5f, child.ComputedStyle.Opacity, 4);
            ui.Screen.Invalidate();
            ui.Render();
            Assert.Equal(0.5f, child.ComputedStyle.Opacity, 4);
        }
    }

    [Fact]
    public void ChangedInheritedValueRefreshesChildAcrossReusedBuffers()
    {
        // A style change must produce the new inherited value on the next pass,
        // never a stale value left over from the previous buffer content.
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.LoadStyles(".parent { width: 100px; height: 100px; }");
        var parent = new Panel { TagName = "div" };
        parent.AddClass("parent");
        var child = new Panel { TagName = "div" };
        parent.AddChild(child);
        ui.Screen.AddChild(parent);
        ui.Render();

        Assert.Equal(1f, child.ComputedStyle.Opacity, 4);

        parent.SetInlineStyle("opacity", "0.4");
        ui.Render();
        Assert.Equal(0.4f, child.ComputedStyle.Opacity, 4);

        parent.SetInlineStyle("opacity", "0.2");
        ui.Render();
        Assert.Equal(0.2f, child.ComputedStyle.Opacity, 4);
    }

    [Fact]
    public void LayoutChangeIsDetectedAfterRepeatedStablePasses()
    {
        // The equal path adopts the compute buffer as the visible style, so the
        // panel's previous and current styles must still be distinct objects
        // when the cascade compares them — otherwise a real layout-affecting
        // change would be missed after a run of unchanged passes.
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.LoadStyles(".box { width: 100px; height: 20px; background-color: #0000ff; }");
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        ui.Screen.AddChild(box);
        ui.Render();

        Assert.Equal(100f, box.Layout.Width, 3);

        for (var i = 0; i < 3; i++)
        {
            ui.Screen.Invalidate();
            ui.Render();
        }

        box.SetInlineStyle("width", "160px");
        ui.Render();
        Assert.Equal(160f, box.Layout.Width, 3);
    }

    [Fact]
    public void PaintOnlyChangeDoesNotEscapeToLayout()
    {
        // A paint-only property (background-color) changing must re-cascade but
        // not reflow: the layout-affecting comparison must still report the
        // box as unchanged after the buffer is adopted.
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.LoadStyles(".box { width: 100px; height: 20px; background-color: #0000ff; }");
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        ui.Screen.AddChild(box);
        ui.Render();

        Assert.Equal(100f, box.Layout.Width, 3);

        box.SetInlineStyle("background-color", "#ff0000");
        ui.Render();

        Assert.Equal(new UiColor(255, 0, 0, 255), box.ComputedStyle.BackgroundColor);
        Assert.Equal(100f, box.Layout.Width, 3);
    }
}
