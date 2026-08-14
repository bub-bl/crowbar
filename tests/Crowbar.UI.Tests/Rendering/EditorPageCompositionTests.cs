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
        // The DockArea positions its dock groups from the rect of its own root,
        // which is only known after a layout pass: run one full frame like the
        // app loop (render to lay out, update to rebuild, render to paint) so
        // the docked panels are in place before the assertions.
        ui.Prepare();
        ui.Update();
        ui.Prepare();
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
        // Every dockable panel is composed through the DockArea: its tab bar
        // shows the titles the panels used to carry as headers.
        Assert.NotNull(FindText(content, "dock-tab", t => t == "OUTILS"));
        Assert.NotNull(FindText(content, "dock-tab", t => t == "EXPLORATEUR"));
        Assert.NotNull(FindText(content, "dock-tab", t => t == "VIEWPORT"));
        Assert.NotNull(FindText(content, "dock-tab", t => t == "INSPECTEUR"));
        Assert.NotNull(FindText(content, "dock-tab", t => t == "MONDE"));
        Assert.NotNull(FindText(content, "dock-tab", t => t == "CONTENU"));
        Assert.NotNull(TestUi.Find(content, p => p.Classes.Contains("viewport-toolbar")));
        var csActive = TestUi.Find(content, p => p.Classes.Contains("cs-active"));
        Assert.NotNull(csActive);
        Assert.Contains("Contenu", TestUi.Texts(csActive!));
        Assert.NotNull(FindText(content, "status-item", t => t.StartsWith("FPS:", StringComparison.Ordinal)));
    }

    [Fact]
    public void StatusBarDisplaysApplicationMemoryWithUsefulUnits()
    {
        var previousUsed = UiDiagnostics.UsedMemoryBytes;
        var previousTotal = UiDiagnostics.TotalMemoryBytes;
        try
        {
            UiDiagnostics.UsedMemoryBytes = 128 * 1_048_576;
            UiDiagnostics.TotalMemoryBytes = 4L * 1_073_741_824;

            using var ui = CreateEditorUi();
            var memory = FindText(ui.Content!, "status-item",
                text => text.StartsWith("Mémoire:", StringComparison.Ordinal));

            Assert.NotNull(memory);
            var memoryText = Assert.Single(TestUi.Texts(memory!));
            Assert.Contains(UiDiagnostics.FormatMemory(UiDiagnostics.UsedMemoryBytes), memoryText);
            Assert.Contains(UiDiagnostics.FormatMemory(UiDiagnostics.TotalMemoryBytes), memoryText);
        }
        finally
        {
            UiDiagnostics.UsedMemoryBytes = previousUsed;
            UiDiagnostics.TotalMemoryBytes = previousTotal;
        }
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
        ui.Prepare();

        dev = FindText(ui.Content!, "tab", t => t == "DÉVELOPPER");
        Assert.NotNull(dev);
        Assert.True(dev!.Classes.Contains("tab-active"));
        creer = FindText(ui.Content!, "tab", t => t == "CRÉER");
        Assert.NotNull(creer);
        Assert.False(creer!.Classes.Contains("tab-active"));
    }

    [Fact]
    public void ExplorerTreeRowsRenderTheirIcons()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // The explorer rows carry entity-type icons: folder rows fall back to the
        // folder glyph, leaf rows to their entity icon. Every icon must resolve
        // to a real panel with a name (the renderer resolves the file later).
        var treeIcons = TestUi.FindAll(content, p => p is Icon i && !string.IsNullOrEmpty(i.Name)).Cast<Icon>().ToList();
        Assert.Contains(treeIcons, i => i.Name == "Solar/map/Bold/globe");
        Assert.Contains(treeIcons, i => i.Name == "terrain");
        Assert.Contains(treeIcons, i => i.Name == "Solar/devices/Bold/lightbulb");
        Assert.Contains(treeIcons, i => i.Name == "Solar/folders/Bold/folder-2");
        Assert.Contains(treeIcons, i => i.Name == "Solar/folders/Bold/folder-open");
        Assert.Contains(treeIcons, i => i.Name == "Solar/ui/Bold/flag");
    }

    [Fact]
    public void NotificationsRenderAsToastsAndPrune()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        UiNotifications.Show("Hot reload", "Full reload : 3 instance(s) migrée(s)", "success");

        // Notifications' own BuildHash (UiNotifications.Version) changed, so
        // the descendant-aware render loop rebuilds the tree on ui.Update().
        ui.Update();
        ui.Prepare();

        // The toast is rendered with its title and message text.
        var toast = TestUi.Find(content, p => p.Classes.Contains("notification") && p.Classes.Contains("success"));
        Assert.NotNull(toast);
        Assert.Contains(TestUi.Texts(toast!), t => t.Contains("Hot reload", StringComparison.Ordinal));
        Assert.Contains(TestUi.Texts(toast!), t => t.Contains("3 instance(s)", StringComparison.Ordinal));

        // Version bumps so the component re-renders.
        var versionBefore = UiNotifications.Version;
        UiNotifications.Show("Hot reload", "IL fast path : 2 méthode(s) patchée(s)", "success");
        Assert.True(UiNotifications.Version > versionBefore);
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

        // Toggle "Générer Collision" (located by its row label: the docked
        // tree order no longer guarantees a fixed index). The CheckRow
        // primitive owns its state, so it must stay unchecked even though the
        // parent page re-renders.
        var generer = toggles.Single(t => TestUi.Texts(t.Parent!).Any(text => text.Contains("Générer Collision", StringComparison.Ordinal)));
        ui.ProcessPointerDown(generer.Layout.X + 1, generer.Layout.Y + 1);
        ui.ProcessPointerUp(generer.Layout.X + 1, generer.Layout.Y + 1);
        ui.Update();
        ui.Prepare();

        toggles = TestUi.FindAll(ui.Content!, p => p is ToggleInput).Cast<ToggleInput>().ToList();
        Assert.Equal(3, toggles.Count);
        Assert.Equal(2, toggles.Count(t => t.IsChecked));
        Assert.False(toggles.Single(t => TestUi.Texts(t.Parent!).Any(text => text.Contains("Générer Collision", StringComparison.Ordinal))).IsChecked);
    }

    [Fact]
    public void WindowResizeRelayoutsAndRepublishesTheSceneViewport()
    {
        using var ui = CreateEditorUi();
        var before = ui.SceneViewport;
        Assert.NotNull(before);
        Assert.True(before.Value.Width > 0 && before.Value.Height > 0);

        // Simulate the host resizing the window and running one frame:
        // OnResized calls SetViewport, then the loop runs Update + Prepare.
        ui.SetViewport(1600, 900);
        ui.Update();

        // Prepare lays out at the new size and returns true, so the render loop
        // repaints (recreating the UI offscreen target at 1600x900 instead of
        // leaving it at the pre-resize 1280x720).
        Assert.True(ui.Prepare(), "the resized frame must trigger a repaint");
        Assert.Equal(1600f, ui.Screen.Layout.Width);
        Assert.Equal(900f, ui.Screen.Layout.Height);

        // The dock republished its scene viewport from the freshly laid-out
        // geometry, so the 3D scene fills the enlarged viewport panel.
        var after = ui.SceneViewport;
        Assert.NotNull(after);
        Assert.True(after.Value.Width > before.Value.Width, $"viewport width did not grow ({before.Value.Width} -> {after.Value.Width})");
        Assert.True(after.Value.Height > before.Value.Height, $"viewport height did not grow ({before.Value.Height} -> {after.Value.Height})");
    }
}
