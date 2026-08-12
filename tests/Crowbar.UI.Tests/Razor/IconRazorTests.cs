using Crowbar.UI;

namespace Crowbar.UI.Tests.Razor;

/// <summary>
/// The <c>&lt;icon name="..."&gt;</c> element resolves an SVG from
/// <c>Assets/Icons</c> (rasterized and tinted by the renderer). The name is
/// stored on the panel and can carry subfolder paths (e.g. the Solar pack
/// lives at <c>Solar/&lt;category&gt;/Bold/&lt;name&gt;</c>).
/// </summary>
public class IconRazorTests
{
    [Fact]
    public void IconElementStoresItsName()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root">
              <icon name="translate" />
            </div>
            """, "IconNameDemo");
        ui.Render();

        var icon = TestUi.Find(ui.Screen, p => p is Icon);
        Assert.NotNull(icon);
        Assert.Equal("translate", ((Icon)icon!).Name);
        // The name is not a generic attribute: it must not pollute the
        // attribute map used by attribute selectors.
        Assert.False(icon.Attributes.ContainsKey("name"));
    }

    [Fact]
    public void IconNameSupportsSubfolderPaths()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root">
              <icon name="Solar/ui/Bold/cursor" />
            </div>
            """, "IconSubfolderDemo");
        ui.Render();

        var icon = TestUi.Find(ui.Screen, p => p is Icon);
        Assert.NotNull(icon);
        Assert.Equal("Solar/ui/Bold/cursor", ((Icon)icon!).Name);
    }

    [Fact]
    public void IconWithoutNameRendersWithoutCrashing()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root">
              <icon />
            </div>
            """, "IconNoNameDemo");
        // Must not throw even though the icon has nothing to draw.
        ui.Render();
    }

    [Fact]
    public void IconSurvivesSplatting()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root">
              <icon @attributes="attrs" />
            </div>
            @code {
                private readonly Dictionary<string, object> attrs = new()
                {
                    ["name"] = "settings"
                };
            }
            """, "IconSplatDemo");
        ui.Render();

        var icon = TestUi.Find(ui.Screen, p => p is Icon);
        Assert.NotNull(icon);
        Assert.Equal("settings", ((Icon)icon!).Name);
    }
}
