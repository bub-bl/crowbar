using Crowbar.UI;

namespace Crowbar.UI.Tests.Razor;

public class RazorRoutingTests
{
    [Fact]
    public void RoutesConstraintsAnd404Fallback()
    {
        using var dir = TestUi.TempDir("CrowbarRazorPages");
        dir.Write("Home.razor", "@page \"/\"\n@page \"/home\"\n<div class=\"home-page\"><span>Home</span></div>\n");
        dir.Write("Item.razor", "@page \"/items/{id:int}\"\n<div><span>Item @Id</span></div>\n@code {\n    [Microsoft.AspNetCore.Components.Parameter] public int Id { get; set; }\n}\n");
        dir.Write("Nav.razor", "@page \"/nav\"\n<div><button @onclick=\"GoHome\">Back</button></div>\n@code {\n    private void GoHome() { NavigateTo(\"/home\"); }\n}\n");

        using var ui = TestUi.Create();
        ui.RegisterRazorComponentsFromDirectory(dir.Path);
        Assert.Equal(4, ui.Pages.Count);

        ui.Navigate("/");
        ui.Prepare();
        Assert.Equal("/", ui.CurrentUrl);
        Assert.NotNull(TestUi.Find(ui.Screen, p => p.Text == "Home"));

        ui.Navigate("/home");
        ui.Prepare();
        Assert.NotNull(TestUi.Find(ui.Screen, p => p.Text == "Home"));

        ui.Navigate("/items/42");
        ui.Prepare();
        Assert.NotNull(TestUi.Find(ui.Screen, p => p.Text == "Item 42"));

        ui.Navigate("/items/abc");
        ui.Prepare();
        Assert.NotNull(TestUi.Find(ui.Screen, p => p.Text.Contains("404")));

        ui.Navigate("/missing");
        ui.Prepare();
        Assert.Equal("/missing", ui.CurrentUrl);
        Assert.NotNull(TestUi.Find(ui.Screen, p => p.Text.Contains("404")));

        ui.Navigate("/nav");
        ui.Prepare();
        var button = TestUi.Find(ui.Screen, p => p is Button);
        Assert.NotNull(button);
        ui.ProcessPointerDown(button.Layout.X + 1, button.Layout.Y + 1);
        ui.ProcessPointerUp(button.Layout.X + 1, button.Layout.Y + 1);
        ui.Update();
        ui.Prepare();
        Assert.Equal("/home", ui.CurrentUrl);
        Assert.NotNull(TestUi.Find(ui.Screen, p => p.Text == "Home"));
    }

    [Fact]
    public void SingleFileRegistrationRegistersItsPages()
    {
        using var dir = TestUi.TempDir("CrowbarRazorSingle");
        var path = dir.Write("Home.razor", "@page \"/\"\n@page \"/home\"\n<div><span>Home</span></div>\n");
        using var ui = TestUi.Create();
        ui.RegisterRazorComponentFromFile("Solo", path, "Solo");
        Assert.Equal(2, ui.Pages.Count);
    }
}
