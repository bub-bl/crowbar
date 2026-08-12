using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// Renders the real editor page (Editor.razor composed of the reusable panels
/// in Ui/EditorPanels and primitives in Ui/Components) through the full
/// pipeline. Guards the component split: every panel must still be composed
/// from the page, scoped styles must apply across component boundaries, and
/// the panels' own interactions (tabs, checkboxes) must keep working after a
/// re-render.
/// </summary>
public class EditorPageCompositionTests
{
    internal static UiSystem CreateEditorUi()
    {
        var uiDir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "src", "Editor", "Ui"));
        Assert.True(Directory.Exists(uiDir), $"Ui directory not found: {uiDir}");
        var ui = new UiSystem();
        ui.SetViewport(1280, 720);
        ui.RegisterRazorComponentsFromDirectory(uiDir);
        ui.Navigate("/editor");
        ui.Render();
        return ui;
    }

    /// <summary>Text lives on child text panels, so match against the descendant text.</summary>
    private static Panel? FindText(Panel root, string className, Func<string, bool> match) =>
        TestUi.FindAll(root, p => p.Classes.Contains(className)).FirstOrDefault(p => TestUi.Texts(p).Any(match));

    [Fact]
    public void EditorPageComposesAllEightPanels()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        Assert.NotNull(FindText(content, "logo", t => t == "Crowbar"));
        Assert.NotNull(FindText(content, "panel-title", t => t == "OUTILS"));
        Assert.NotNull(FindText(content, "panel-title", t => t == "EXPLORATEUR"));
        Assert.NotNull(TestUi.Find(content, p => p.Classes.Contains("viewport-toolbar")));
        Assert.NotNull(FindText(content, "panel-title", t => t == "Maison_Bois"));
        Assert.NotNull(FindText(content, "panel-title", t => t == "PARAMÈTRES DU MONDE"));
        var csActive = TestUi.Find(content, p => p.Classes.Contains("cs-active"));
        Assert.NotNull(csActive);
        Assert.Contains("Contenu", TestUi.Texts(csActive!));
        Assert.NotNull(FindText(content, "status-item", t => t.StartsWith("FPS:", StringComparison.Ordinal)));
    }

    [Fact]
    public void ScopedStylesApplyAcrossTheComponentBoundary()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // The top bar's own scoped CSS paints its component root charcoal.
        var topbar = TestUi.Find(content, p => p.HasScope("b-topbar"));
        Assert.NotNull(topbar);
        Assert.Equal(new UiColor(17, 17, 17, 255), topbar!.ComputedStyle.BackgroundColor);

        // The selected tree row (TreeRow primitive inside ExplorerPanel) is
        // accent blue, exactly like the original single-page layout.
        var selected = FindText(content, "tree-selected", t => t.Contains("Maison_Bois", StringComparison.Ordinal));
        Assert.NotNull(selected);
        Assert.Equal(new UiColor(47, 111, 224, 255), selected!.ComputedStyle.BackgroundColor);

        // The material thumbnail color class comes from a parameter.
        var vitre = TestUi.Find(content, p => p.Classes.Contains("mat-thumb") && p.Classes.Contains("mat-glass"));
        Assert.NotNull(vitre);
        Assert.Equal(new UiColor(143, 183, 214, 255), vitre!.ComputedStyle.BackgroundColor);
    }

    [Fact]
    public void TopBarTabClickMarksTheTabActive()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // CRÉER is the default active tab.
        var creer = FindText(content, "tab", t => t == "CRÉER");
        Assert.NotNull(creer);
        Assert.True(creer!.Classes.Contains("tab-active"));

        // Click DÉVELOPPER: the TopBar component re-renders itself.
        var dev = FindText(content, "tab", t => t == "DÉVELOPPER");
        Assert.NotNull(dev);
        ui.ProcessPointerDown(dev!.Layout.X + 1, dev.Layout.Y + 1);
        ui.ProcessPointerUp(dev.Layout.X + 1, dev.Layout.Y + 1);
        ui.Update();
        ui.Render();

        dev = FindText(ui.Content!, "tab", t => t == "DÉVELOPPER");
        Assert.NotNull(dev);
        Assert.True(dev!.Classes.Contains("tab-active"));
        creer = FindText(ui.Content!, "tab", t => t == "CRÉER");
        Assert.NotNull(creer);
        Assert.False(creer!.Classes.Contains("tab-active"));
    }

    [Fact]
    public void CheckboxRowsKeepTheirOwnStateAcrossReRenders()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // Three checkboxes: Actif (inspector head), Générer Collision
        // (inspector body), Brouillard (world panel). All start checked.
        var toggles = TestUi.FindAll(content, p => p is ToggleInput).Cast<ToggleInput>().ToList();
        Assert.Equal(3, toggles.Count);
        Assert.All(toggles, t => Assert.True(t.IsChecked));

        // Toggle "Générer Collision": the CheckRow primitive owns its state, so
        // it must stay unchecked even though the parent page re-renders.
        ui.ProcessPointerDown(toggles[1].Layout.X + 1, toggles[1].Layout.Y + 1);
        ui.ProcessPointerUp(toggles[1].Layout.X + 1, toggles[1].Layout.Y + 1);
        ui.Update();
        ui.Render();

        toggles = TestUi.FindAll(ui.Content!, p => p is ToggleInput).Cast<ToggleInput>().ToList();
        Assert.Equal(3, toggles.Count);
        Assert.True(toggles[0].IsChecked);
        Assert.False(toggles[1].IsChecked);
        Assert.True(toggles[2].IsChecked);
    }
}
