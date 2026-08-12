using Crowbar.UI;

namespace Crowbar.UI.Tests.Styling;

/// <summary>
/// Guards the scoped style re-cascade: a panel's state change (hover, pressed,
/// class, inline style) must re-cascade exactly the affected set — the mutated
/// panel's parent subtree — instead of the whole tree, while still producing
/// the same computed styles for descendant and sibling selectors.
/// </summary>
public class ScopedCascadeTests
{
    private const int Width = 320;
    private const int Height = 200;

    [Fact]
    public void AncestorHoverRecascadesDescendants()
    {
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.LoadStyles(".outer { width: 200px; height: 100px; } .outer:hover .inner { color: #00ff00; }");
        var outer = new Panel { TagName = "div" };
        outer.AddClass("outer");
        var inner = new Panel { TagName = "div" };
        inner.AddClass("inner");
        outer.AddChild(inner);
        ui.Screen.AddChild(outer);
        ui.Render();

        Assert.Equal(UiColor.White, inner.ComputedStyle.Color);

        // Only the hovered panel is a style root; the descendant is not.
        outer.SetHovered(true);
        Assert.Equal([outer], ui.Screen.StyleDirtyRoots);
        ui.Render();

        // .outer:hover .inner must still re-match even though `inner` itself
        // was not marked dirty.
        Assert.Equal(new UiColor(0, 255, 0, 255), inner.ComputedStyle.Color);
    }

    [Fact]
    public void SiblingCombinatorRecascadesSibling()
    {
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.LoadStyles(".a { width: 50px; height: 20px; } .a:hover + .b { color: #ff0000; }");
        var container = new Panel { TagName = "div" };
        var a = new Panel { TagName = "div" };
        a.AddClass("a");
        var b = new Panel { TagName = "div" };
        b.AddClass("b");
        container.AddChild(a);
        container.AddChild(b);
        ui.Screen.AddChild(container);
        ui.Render();

        Assert.Equal(UiColor.White, b.ComputedStyle.Color);

        a.SetHovered(true);
        ui.Render();

        // The re-cascade walks the parent's subtree, so the adjacent sibling
        // re-matches.
        Assert.Equal(new UiColor(255, 0, 0, 255), b.ComputedStyle.Color);
    }

    [Fact]
    public void UnrelatedSubtreeKeepsItsStyle()
    {
        using var ui = TestUi.Create(width: Width, height: Height);
        ui.LoadStyles(".box { width: 100px; height: 40px; } .box:hover { background-color: #00ff00; }");
        var left = new Panel { TagName = "div" };
        left.AddClass("box");
        var right = new Panel { TagName = "div" };
        right.AddClass("box");
        ui.Screen.AddChild(left);
        ui.Screen.AddChild(right);
        ui.Render();

        Assert.Equal(UiColor.Transparent, right.ComputedStyle.BackgroundColor);

        left.SetHovered(true);
        ui.Render();

        Assert.Equal(new UiColor(0, 255, 0, 255), left.ComputedStyle.BackgroundColor);
        Assert.Equal(UiColor.Transparent, right.ComputedStyle.BackgroundColor);
    }

    [Fact]
    public void StyleDirtyRootsAreClearedAfterRender()
    {
        using var ui = TestUi.Create(width: Width, height: Height);
        var panel = new Panel { TagName = "div" };
        panel.AddClass("box");
        ui.Screen.AddChild(panel);
        ui.Render();

        panel.SetHovered(true);
        Assert.Single(ui.Screen.StyleDirtyRoots);

        ui.Render();
        Assert.Empty(ui.Screen.StyleDirtyRoots);
    }

    [Fact]
    public void StyleChangeWhileInheritedAnimationRunsStillRefreshesChildren()
    {
        // Regression: the scoped cascade no longer walks the whole tree, so an
        // inherited-property animation on a panel outside the dirty subtrees
        // must still trigger the inheritance-only refresh in the same frame as
        // a style change (previously the style pass swallowed the flag).
        Keyframes.Define("cb-scope-fade",
            KeyframeFrame.At(0, ("opacity", "0")),
            KeyframeFrame.At(1, ("opacity", "1")));
        try
        {
            using var ui = TestUi.Create(width: Width, height: Height);
            ui.LoadStyles(
                ".fade { animation: cb-scope-fade 0.6s ease-out both; width: 120px; height: 24px; }" +
                ".btn { width: 40px; height: 20px; } .btn:hover { background-color: #ff0000; }");
            var fade = new Panel { TagName = "div" };
            fade.AddClass("fade");
            var fadeChild = new Panel { TagName = "div" };
            fadeChild.AddChild(new Label("X"));
            fade.AddChild(fadeChild);
            var btn = new Panel { TagName = "div" };
            btn.AddClass("btn");
            ui.Screen.AddChild(fade);
            ui.Screen.AddChild(btn);
            ui.Render();
            var label = (Label)fadeChild.Children[0];

            // Mid-animation, hover the unrelated button in the same frame.
            ui.Update(0.1f);
            btn.SetHovered(true);
            ui.Render();

            Assert.Equal(new UiColor(255, 0, 0, 255), btn.ComputedStyle.BackgroundColor);
            Assert.Equal(fade.ComputedStyle.Opacity, label.ComputedStyle.Opacity, 3);
        }
        finally
        {
            Keyframes.Remove("cb-scope-fade");
        }
    }
}
