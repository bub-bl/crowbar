using Crowbar.UI;

namespace Crowbar.UI.Tests.Razor;

public class AutoRegisterTests
{
    [Fact]
    public void DirectoryRegistrationSkipsUnderscoreFiles()
    {
        using var dir = TestUi.TempDir("CrowbarRazorComponents");
        dir.Write("AutoLabel.razor", """
            <div class="auto-box"><span>@Label</span></div>
            @code {
                [Microsoft.AspNetCore.Components.Parameter] public string Label { get; set; } = string.Empty;
            }
            """);
        dir.Write("_Imports.razor", "@inherits Crowbar.UI.RazorPanel");

        using var ui = TestUi.Create();
        var registered = ui.RegisterRazorComponentsFromDirectory(dir.Path);
        Assert.Equal(1, registered);

        ui.LoadRazor("<AutoLabel Label=\"auto resolved\" />", "AutoRoot");
        ui.Prepare();
        Assert.NotNull(TestUi.Find(ui.Screen, p => p.Text == "auto resolved"));
    }

    [Fact]
    public void DirectoryRegistrationCollidingWithManualComponentThrows()
    {
        using var dir = TestUi.TempDir("CrowbarRazorCollision");
        dir.Write("Widget.razor", "<div>from directory</div>");

        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("Widget", "<div>manual</div>", "Widget");
        var ex = Assert.Throws<InvalidOperationException>(() => ui.RegisterRazorComponentsFromDirectory(dir.Path));
        Assert.Contains("Duplicate Razor component tag", ex.Message);
    }
}
