using Crowbar.UI;

namespace Crowbar.UI.Tests.Razor;

public class ScopedCssTests
{
    [Fact]
    public void ScopedStylesStopAtComponentBoundary()
    {
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("ChildComp", """
            <div class="box"><span class="scoped-label">Child</span></div>
            """, "ChildComp", cssSource: """
            root { width: 50px; }
            .box { width: 50px; }
            .scoped-label { color: #00ff00; }
            """);
        ui.LoadRazor("""
            <div class="parent-box">
              <span class="scoped-label">Parent</span>
              <ChildComp />
            </div>
            """, "ParentComp", cssSource: """
            root { width: 99px; height: 99px; }
            .scoped-label { color: #ff0000; }
            """);
        ui.Prepare();

        var parentLabel = TestUi.Find(ui.Screen, p => p.Text == "Parent");
        var childLabel = TestUi.Find(ui.Screen, p => p.Text == "Child");
        Assert.NotNull(parentLabel);
        Assert.NotNull(childLabel);

        Assert.Equal(new UiColor(255, 0, 0, 255), parentLabel.ComputedStyle.Color);
        Assert.Equal(new UiColor(0, 255, 0, 255), childLabel.ComputedStyle.Color);

        // The parent's scoped `root` rule must not leak into the child root.
        var childRoot = TestUi.Find(ui.Screen, p => p.TagName == "root" && !ReferenceEquals(p, ui.Content));
        Assert.NotNull(childRoot);
        Assert.Equal(CssLength.Points(50), childRoot.ComputedStyle.Width);
        Assert.NotEqual(CssLength.Points(99), childRoot.ComputedStyle.Height);
    }

    [Fact]
    public void ScopedHoverWithShorthandColorApplies()
    {
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("HoverBtn", """
            <div class="btn"><div class="title">@Label</div></div>
            @code {
                [Microsoft.AspNetCore.Components.Parameter] public string Label { get; set; } = string.Empty;
            }
            """, "HoverBtn", cssSource: """
            root { width: 300px; height: 40px; }
            .btn { width: 300px; height: 40px; background-color: black; }
            .btn:hover { background-color: #333; }
            .title { color: #333; }
            """);
        ui.LoadRazor("""
            <div class="root"><HoverBtn Label="hi" /></div>
            """, "HoverShorthandDemo");
        ui.Prepare();

        var btn = TestUi.Find(ui.Screen, p => p.Classes.Contains("btn"));
        var title = TestUi.Find(ui.Screen, p => p.Classes.Contains("title"));
        Assert.NotNull(btn);
        Assert.NotNull(title);
        Assert.Equal(new UiColor(0, 0, 0, 255), btn.ComputedStyle.BackgroundColor);
        Assert.Equal(new UiColor(51, 51, 51, 255), title.ComputedStyle.Color);

        ui.ProcessPointerMove(btn.Layout.X + btn.Layout.Width / 2, btn.Layout.Y + btn.Layout.Height / 2);
        ui.Prepare();
        Assert.True(btn.IsHovered);
        Assert.Equal(new UiColor(51, 51, 51, 255), btn.ComputedStyle.BackgroundColor);

        ui.ProcessPointerMove(639, 199);
        ui.Prepare();
        Assert.Equal(new UiColor(0, 0, 0, 255), btn.ComputedStyle.BackgroundColor);
    }
}
