using Crowbar.UI;

namespace Crowbar.UI.Tests.Razor;

/// <summary>
/// The <c>tooltip</c> attribute is a hover overlay, not a [Parameter] or a
/// generic attribute: it is stored on the panel and the renderer draws it
/// near the cursor while the panel (or a descendant) is hovered.
/// </summary>
public class TooltipTests
{
    [Fact]
    public void TooltipAttributeIsStoredOnAnElement()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root">
              <button tooltip="Delete the item">X</button>
            </div>
            """, "TooltipElementDemo");
        ui.Prepare();
        var button = TestUi.Find(ui.Screen, p => p is Button);
        Assert.NotNull(button);
        Assert.Equal("Delete the item", button!.Tooltip);
        // The tooltip is not a generic attribute: it must not pollute the
        // attribute map used by attribute selectors.
        Assert.False(button.Attributes.ContainsKey("tooltip"));
    }

    [Fact]
    public void TooltipAttributeIsStoredOnAComponentRoot()
    {
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("PlainCard", """
            <div class="card"><span>card</span></div>
            """, "PlainCard");
        ui.LoadRazor("""
            <div class="root">
              <PlainCard tooltip="A reusable card" />
            </div>
            """, "TooltipComponentDemo");
        ui.Prepare();
        // The tooltip lands on the component's root panel (the attribute is
        // stored before the template builds its children).
        var card = TestUi.Find(ui.Screen, p => p.Tooltip == "A reusable card");
        Assert.NotNull(card);
    }

    [Fact]
    public void TooltipAttributeSurvivesSplatting()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root">
              <div @attributes="attrs">splat</div>
            </div>
            @code {
                private readonly Dictionary<string, object> attrs = new()
                {
                    ["tooltip"] = "from the splat",
                    ["data-extra"] = "hello"
                };
            }
            """, "TooltipSplatDemo");
        ui.Prepare();
        var panel = TestUi.Find(ui.Screen, p => p.Attributes.ContainsKey("data-extra"));
        Assert.NotNull(panel);
        Assert.Equal("from the splat", panel!.Tooltip);
        Assert.False(panel.Attributes.ContainsKey("tooltip"));
    }

    [Fact]
    public void HoveringShowsTheDeepestTooltipAndLeavingHidesIt()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root" style="width: 640px; height: 480px;">
              <div tooltip="outer" style="width: 200px; height: 100px; position: absolute; left: 20px; top: 20px;">
                <div tooltip="inner" style="width: 50px; height: 50px; position: absolute; left: 30px; top: 30px;"></div>
              </div>
            </div>
            """, "TooltipHoverDemo");
        ui.Prepare();

        Assert.Null(ui.Renderer.TooltipText);

        // Hover over the inner panel: the deepest tooltip wins.
        ui.ProcessPointerMove(60, 60);
        Assert.Equal("inner", ui.Renderer.TooltipText);

        // Hover over the outer panel (outside the inner box).
        ui.ProcessPointerMove(30, 30);
        Assert.Equal("outer", ui.Renderer.TooltipText);

        // Leave the panels entirely: the tooltip hides.
        ui.ProcessPointerMove(500, 400);
        Assert.Null(ui.Renderer.TooltipText);
    }

    [Fact]
    public void TooltipTextIsExposedOnTheRenderer()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root" style="width: 640px; height: 480px;">
              <button tooltip="Save changes" style="width: 100px; height: 30px; position: absolute; left: 40px; top: 40px;">save</button>
            </div>
            """, "TooltipRendererDemo");
        ui.Prepare();
        var button = TestUi.Find(ui.Screen, p => p is Button);
        Assert.NotNull(button);
        ui.ProcessPointerMove(button!.Layout.X + 5, button.Layout.Y + 5);
        Assert.Equal("Save changes", ui.Renderer.TooltipText);
    }
}
