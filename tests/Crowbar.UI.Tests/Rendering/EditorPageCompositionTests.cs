using Crowbar.Editor;
using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

// The editor page's Explorer panel reads the process-global
// EditorExplorerState (published by the editor host in the real app). The
// tests that render the page serialize on this collection so their shared
// publishes never interleave.
[Collection("EditorPage")]

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
        ui.RegisterComponent("PropertyEditor", () => new PropertyEditor());
        ui.Navigate("/editor");
        // The Explorer tree is no longer hard-coded: it mirrors the state the
        // editor host publishes from the live world. Publish a demo hierarchy
        // so the tree assertions see the same rows the old fake tree showed.
        // The inspector reads its own snapshot the same way (see
        // PublishDemoInspectorState), so publish both before the first frame.
        // Inspector section ids are stable strings, so reset the shared state
        // first or a collapse left by another test would leak in. The content
        // panel selection is shared the same way. (Notification toasts are
        // cleaned up by the notification tests themselves.)
        EditorInspectorState.Reset();
        EditorContentState.Reset();
        PublishDemoExplorerState();
        PublishDemoInspectorState();
        PublishDemoContentState();
        // The DockArea positions its dock groups from the rect of its own root,
        // which is only known after a layout pass: run one full frame like the
        // app loop (render to lay out, update to rebuild, render to paint) so
        // the docked panels are in place before the assertions.
        ui.Prepare();
        ui.Update();
        ui.Prepare();
        return ui;
    }

    /// <summary>
    /// Publishes a hierarchy shaped like the historical demo tree (world root,
    /// environment/lights/structures folders, props and spawn points) with
    /// Wood_House selected and the Props/Players folders collapsed, so the
    /// tree assertions exercise folders, entity icons and the selection state.
    /// </summary>
    internal static void PublishDemoExplorerState()
    {
        var world = Guid.NewGuid();
        var environment = Guid.NewGuid();
        var light = Guid.NewGuid();
        var structures = Guid.NewGuid();
        var props = Guid.NewGuid();
        var players = Guid.NewGuid();
        var house = Guid.NewGuid();
        var nodes = new List<EditorExplorerState.TreeNode>
        {
            new("World", world, null, "Solar/map/Bold/globe", IsFolder: true),
            new("Environment", environment, world, string.Empty, IsFolder: true),
            new("Terrain", Guid.NewGuid(), environment, "terrain", IsFolder: false),
            new("Water", Guid.NewGuid(), environment, "Solar/sports/Bold/water", IsFolder: false),
            new("Sky", Guid.NewGuid(), environment, "Solar/weather/Bold/cloud", IsFolder: false),
            new("Light", light, world, string.Empty, IsFolder: true),
            new("Directional Light", Guid.NewGuid(), light, "Solar/devices/Bold/lightbulb", IsFolder: false),
            new("Exponential Fog", Guid.NewGuid(), light, "Solar/weather/Bold/fog", IsFolder: false),
            new("Structures", structures, world, string.Empty, IsFolder: true),
            new("Wood_House", house, structures, string.Empty, IsFolder: true),
            new("Floor", Guid.NewGuid(), house, "terrain", IsFolder: false),
            new("Walls", Guid.NewGuid(), house, "Solar/ui/Bold/box-minimalistic", IsFolder: false),
            new("Roof", Guid.NewGuid(), house, "Solar/ui/Bold/box-minimalistic", IsFolder: false),
            new("Door", Guid.NewGuid(), house, "Solar/ui/Bold/box-minimalistic", IsFolder: false),
            new("Window", Guid.NewGuid(), house, "Solar/it/Bold/window-frame", IsFolder: false),
            new("Metal_Hangar", Guid.NewGuid(), structures, "Solar/building/Bold/buildings", IsFolder: false),
            new("Water_Tower", Guid.NewGuid(), structures, "Solar/sports/Bold/water", IsFolder: false),
            new("Props", props, world, string.Empty, IsFolder: true),
            new("Crate_01", Guid.NewGuid(), props, "Solar/ui/Bold/box", IsFolder: false),
            new("Barrel", Guid.NewGuid(), props, "Solar/ui/Bold/box-minimalistic", IsFolder: false),
            new("Palette", Guid.NewGuid(), props, "Solar/tools/Bold/palette", IsFolder: false),
            new("Players", players, world, string.Empty, IsFolder: true),
            new("Spawn_Points", Guid.NewGuid(), players, "Solar/ui/Bold/flag", IsFolder: false)
        };
        // Only Props is collapsed: a collapsed folder must still render the
        // closed-folder glyph (folder-2) while the open Players folder keeps
        // its Spawn_Points leaf (flag) visible.
        EditorExplorerState.Publish(nodes, house);
        EditorExplorerState.ToggleCollapsed(props);
    }

    /// <summary>
    /// Publishes the inspector snapshot the real InspectorStateBuilder would
    /// produce for the selected entity (a transform plus a MeshRenderer with a
    /// Pbr material), so the panel assertions see every editor instead of the
    /// old hard-coded rows.
    /// </summary>
    internal static void PublishDemoInspectorState()
    {
        EditorInspectorState.Publish("House",
        [
            new EditorInspectorState.Section("transform", "Transform", null,
            [
                new EditorInspectorState.Property("Position", "System.Numerics.Vector3", "1245.6, 320.7, 884.2", Key: "transform.position"),
                new EditorInspectorState.Property("Rotation", "System.Numerics.Vector3", "0, 132.5, 0", Key: "transform.rotation"),
                new EditorInspectorState.Property("Scale", "System.Numerics.Vector3", "1, 1, 1", Key: "transform.scale")
            ]),
            new EditorInspectorState.Section("MeshRenderer", "MeshRenderer", "Solar/ui/Bold/box-minimalistic",
            [
                new EditorInspectorState.Property("Material", "System.Object", "StandardPbr"),
                new EditorInspectorState.Property("Color", "System.Numerics.Vector4", "0.2, 0.6, 1, 1", Indent: 1, Key: "MeshRenderer.Material.color"),
                new EditorInspectorState.Property("UV Scale", "System.Numerics.Vector2", "1, 1", Indent: 1, Key: "MeshRenderer.Material.uvScale"),
                new EditorInspectorState.Property("Metallic", "System.Single", "0.15", Indent: 1, Key: "MeshRenderer.Material.metallic"),
                new EditorInspectorState.Property("Model", "System.Object", "house.glb"),
                new EditorInspectorState.Property("Roughness", "System.Single", "0.45", Indent: 1, Key: "MeshRenderer.Material.roughness")
            ])
        ]);
    }

    /// <summary>
    /// Publishes a content snapshot shaped like the demo project's assets
    /// (models in Models/, a sound in Sounds/), so the Content panel asserts
    /// see the tiles the host would publish from the live content folder.
    /// </summary>
    internal static void PublishDemoContentState()
    {
        EditorContentState.Publish(
        [
            new EditorContentState.Entry("Crate.gltf", "Models", "model"),
            new EditorContentState.Entry("industrial_work_light.gltf", "Models", "model"),
            new EditorContentState.Entry("ui_compilation_error.wav", "Sounds", "sound")
        ]);
    }

    /// <summary>Text lives on child text panels, so match against the descendant text.</summary>
    private static Panel? FindText(Panel root, string className, Func<string, bool> match) =>
        TestUi.FindAll(root, p => p.Classes.Contains(className)).FirstOrDefault(p => TestUi.Texts(p).Any(match));

    /// <summary>Finds the first text input whose value equals <paramref name="value"/>.</summary>
    private static TextInput? FindInput(Panel root, string value) =>
        TestUi.FindAll(root, p => p is TextInput).Cast<TextInput>().FirstOrDefault(input => input.Value == value);

    /// <summary>Names of the icon panels under <paramref name="root"/> (the text carets became icons).</summary>
    private static IEnumerable<string> IconsOf(Panel root) =>
        TestUi.FindAll(root, p => p is Icon { Name: not null and not "" }).Select(icon => ((Icon)icon).Name!);

    [Fact]
    public void EditorPageComposesAllDockablePanels()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        Assert.NotNull(FindText(content, "logo", t => t == "Crowbar"));
        // Every dockable panel is composed through the DockArea: its tab bar
        // shows the titles the panels used to carry as headers.
        Assert.NotNull(FindText(content, "dock-tab", t => t == "HIERARCHY"));
        Assert.NotNull(FindText(content, "dock-tab", t => t == "VIEWPORT"));
        Assert.NotNull(FindText(content, "dock-tab", t => t == "INSPECTOR"));
        Assert.NotNull(FindText(content, "dock-tab", t => t == "CONTENT"));
        Assert.NotNull(TestUi.Find(content, p => p.Classes.Contains("viewport-toolbar")));
        var csActive = TestUi.Find(content, p => p.Classes.Contains("cs-active"));
        Assert.NotNull(csActive);
        Assert.Contains("Content", TestUi.Texts(csActive!));
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
                text => text.StartsWith("Memory:", StringComparison.Ordinal));

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
    public void StatusBarRendersGameCodeEntries()
    {
        var previousEntries = UiDiagnostics.StatusBarEntries;
        try
        {
            UiDiagnostics.StatusBarEntries =
            [
                new StatusBarEntry("Game", "Demo — 5 points, 0 reloads"),
                new StatusBarEntry("Lives", "3")
            ];

            using var ui = CreateEditorUi();
            var content = ui.Content!;

            Assert.NotNull(FindText(content, "status-item",
                text => text.StartsWith("Game:", StringComparison.Ordinal) &&
                        text.Contains("Demo — 5 points", StringComparison.Ordinal)));
            Assert.NotNull(FindText(content, "status-item",
                text => text.StartsWith("Lives:", StringComparison.Ordinal)));
        }
        finally
        {
            UiDiagnostics.StatusBarEntries = previousEntries;
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
        var selected = FindText(content, "tree-selected", t => t.Contains("Wood_House", StringComparison.Ordinal));
        Assert.NotNull(selected);
        Assert.Equal(new UiColor(47, 111, 224, 255), selected!.ComputedStyle.BackgroundColor);

        // The inspector's entity-name field is styled by the InspectorPanel's
        // own scoped sheet, across the panel boundary.
        var entityName = TestUi.Find(content, p => p.Classes.Contains("entity-name"));
        Assert.NotNull(entityName);
        Assert.Equal(new UiColor(14, 14, 14, 255), entityName!.ComputedStyle.BackgroundColor);

        // The collapsible section header (InspectorSection primitive) is styled
        // by its own sheet, not the panel's.
        var sectionHead = TestUi.Find(content, p => p.Classes.Contains("insp-section-head"));
        Assert.NotNull(sectionHead);
        Assert.Equal(new UiColor(242, 242, 242, 255), sectionHead!.ComputedStyle.Color);
    }

    [Fact]
    public void TopBarTabClickMarksTheTabActive()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // CREATE is the default active tab.
        var create = FindText(content, "tab", t => t == "CREATE");
        Assert.NotNull(create);
        Assert.True(create!.Classes.Contains("tab-active"));

        // Click DEVELOP: the TopBar component re-renders itself.
        var dev = FindText(content, "tab", t => t == "DEVELOP");
        Assert.NotNull(dev);
        ui.ProcessPointerDown(dev!.Layout.X + 1, dev.Layout.Y + 1);
        ui.ProcessPointerUp(dev.Layout.X + 1, dev.Layout.Y + 1);
        ui.Update();
        ui.Prepare();

        dev = FindText(ui.Content!, "tab", t => t == "DEVELOP");
        Assert.NotNull(dev);
        Assert.True(dev!.Classes.Contains("tab-active"));
        create = FindText(ui.Content!, "tab", t => t == "CREATE");
        Assert.NotNull(create);
        Assert.False(create!.Classes.Contains("tab-active"));
    }

    [Fact]
    public void ContentPanelShowsThePublishedProjectAssets()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // The grid mirrors the published snapshot: category titles and asset
        // tiles with the kind of their registered resource type.
        Assert.NotNull(FindText(content, "grid-row-title", t => t == "MODELS"));
        Assert.NotNull(FindText(content, "grid-row-title", t => t == "SOUNDS"));
        Assert.NotNull(FindText(content, "asset-name", t => t == "Crate.gltf"));
        Assert.NotNull(FindText(content, "asset-name", t => t == "ui_compilation_error.wav"));

        // The sidebar lists the root plus one entry per category.
        var sidebar = TestUi.FindAll(content, p => p.Classes.Contains("cs-item"))
            .SelectMany(TestUi.Texts)
            .ToArray();
        Assert.Contains("Content", sidebar);
        Assert.Contains("Models", sidebar);
        Assert.Contains("Sounds", sidebar);

        // The model tile carries the kind of its registered type (Model).
        var modelThumb = TestUi.FindAll(content, p => p.Classes.Contains("asset-thumb"))
            .First(p => p.Classes.Contains("thumb-model"));
        Assert.NotNull(modelThumb);
    }

    [Fact]
    public void ContentPanelCategoryClickFiltersTheGrid()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        var models = FindText(content, "cs-item", t => t == "Models");
        Assert.NotNull(models);
        ui.ProcessPointerDown(models!.Layout.X + 2, models.Layout.Y + 2);
        ui.ProcessPointerUp(models.Layout.X + 2, models.Layout.Y + 2);
        ui.Update();
        ui.Prepare();

        content = ui.Content!;
        Assert.NotNull(FindText(content, "asset-name", t => t == "Crate.gltf"));
        Assert.Null(FindText(content, "asset-name", t => t == "ui_compilation_error.wav"));
        Assert.NotNull(FindText(content, "breadcrumb", t => t == "Content › Models"));
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
    public void AddComponentMenuClickQueuesTheRequestAndClosesTheMenu()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;
        EditorInspectorState.PublishAvailableComponents(["DemoComponent", "PointLight"]);
        ui.Update();
        ui.Prepare();

        // The menu starts closed; clicking "+ Add component" opens it and lists
        // the component types the host published.
        var addButton = FindText(content, "add-component", t => t.Contains("Add component"));
        Assert.NotNull(addButton);
        Assert.False(EditorInspectorState.AddMenuOpen);

        ui.ProcessPointerDown(addButton!.Layout.X + 3, addButton.Layout.Y + 3);
        ui.ProcessPointerUp(addButton.Layout.X + 3, addButton.Layout.Y + 3);
        ui.Update();
        ui.Prepare();

        Assert.True(EditorInspectorState.AddMenuOpen);
        content = ui.Content!;
        Assert.NotEmpty(TestUi.FindAll(content, p => p.Classes.Contains("add-menu")));
        var demoItem = FindText(content, "add-menu-item", t => t == "DemoComponent");
        Assert.NotNull(demoItem);
        Assert.NotNull(FindText(content, "add-menu-item", t => t == "PointLight"));

        // Clicking an item queues the request for the host (the name travels as
        // the child component's [Parameter], not through an @onclick lambda) and
        // closes the menu.
        ui.ProcessPointerDown(demoItem!.Layout.X + 3, demoItem.Layout.Y + 3);
        ui.ProcessPointerUp(demoItem.Layout.X + 3, demoItem.Layout.Y + 3);
        ui.Update();
        ui.Prepare();

        Assert.Equal(["DemoComponent"], EditorInspectorState.ConsumeAddComponentRequests());
        Assert.False(EditorInspectorState.AddMenuOpen);
        content = ui.Content!;
        Assert.Empty(TestUi.FindAll(content, p => p.Classes.Contains("add-menu")));
    }

    [Fact]
    public void NotificationsRenderAsToastsAndPrune()
    {
        // The toasts this test shows are process-global: they must not leak
        // into the next test of the collection (a leftover toast overlays the
        // page and swallows clicks), so they are cleared on the way out.
        try
        {
            using var ui = CreateEditorUi();
            var content = ui.Content!;

            UiNotifications.Show("Hot reload", "Full reload: 3 instance(s) migrated", "success");

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
            UiNotifications.Show("Hot reload", "IL fast path: 2 method(s) patched", "success");
            Assert.True(UiNotifications.Version > versionBefore);
        }
        finally
        {
            UiNotifications.Reset();
        }
    }

    [Fact]
    public void InspectorSectionsCollapseAndStayCollapsedAcrossRepublish()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // The Transform section starts open: its Position/Rotation/Scale rows
        // are visible and the caret icon points down (text carets were replaced
        // by arrow icons, so the icon name is asserted, not a glyph).
        var transformHead = TestUi.FindAll(content, p => p.Classes.Contains("insp-section-head"))
            .Single(head => TestUi.Texts(head).Any(t => t == "Transform"));
        Assert.Contains("Solar/arrows/Bold/alt-arrow-down", IconsOf(transformHead));
        Assert.NotNull(TestUi.Find(content, p => TestUi.Texts(p).Any(t => t == "Position")));

        // Click the header: the body collapses and the caret flips.
        ui.ProcessPointerDown(transformHead.Layout.X + 1, transformHead.Layout.Y + 1);
        ui.ProcessPointerUp(transformHead.Layout.X + 1, transformHead.Layout.Y + 1);
        ui.Update();
        ui.Prepare();

        Assert.Null(TestUi.Find(ui.Content!, p => TestUi.Texts(p).Any(t => t == "Position")));
        transformHead = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("insp-section-head"))
            .Single(head => TestUi.Texts(head).Any(t => t == "Transform"));
        Assert.Contains("Solar/arrows/Bold/alt-arrow-right", IconsOf(transformHead));

        // Republishing the same snapshot (the host does this every frame) must
        // not reopen the section the user folded.
        PublishDemoInspectorState();
        ui.Update();
        ui.Prepare();

        Assert.Null(TestUi.Find(ui.Content!, p => TestUi.Texts(p).Any(t => t == "Position")));
        Assert.True(EditorInspectorState.IsCollapsed("transform"));
    }

    [Fact]
    public void InspectorReflectsThePublishedSelection()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // The published entity name is shown, and its transform + component
        // sections carry the real labels and values.
        Assert.NotNull(TestUi.Find(content, p => p.Classes.Contains("entity-name") && TestUi.Texts(p).Contains("House")));
        Assert.NotNull(FindText(content, "insp-section-head", t => t == "Transform"));
        Assert.NotNull(FindText(content, "insp-section-head", t => t == "MeshRenderer"));
        Assert.NotNull(TestUi.Find(content, p => TestUi.Texts(p).Any(t => t == "house.glb")));

        // Publishing a different selection replaces the panel content.
        EditorInspectorState.Publish("Sun",
        [
            new EditorInspectorState.Section("transform", "Transform", null,
            [
                new EditorInspectorState.Property("Position", "System.Numerics.Vector3", "0, 0, 0"),
                new EditorInspectorState.Property("Rotation", "System.Numerics.Vector3", "-45, -35, 0"),
                new EditorInspectorState.Property("Scale", "System.Numerics.Vector3", "1, 1, 1")
            ]),
            new EditorInspectorState.Section("DirectionalLight", "DirectionalLight", "Solar/devices/Bold/lightbulb",
            [
                new EditorInspectorState.Property("Color", "System.Numerics.Vector3", "1, 0.95, 0.85"),
                new EditorInspectorState.Property("Intensity", "System.Single", "1.6")
            ])
        ]);
        ui.Update();
        ui.Prepare();

        content = ui.Content!;
        Assert.NotNull(TestUi.Find(content, p => p.Classes.Contains("entity-name") && TestUi.Texts(p).Contains("Sun")));
        Assert.NotNull(FindText(content, "insp-section-head", t => t == "DirectionalLight"));
        Assert.Null(FindText(content, "insp-section-head", t => t == "MeshRenderer"));
        Assert.NotNull(FindInput(content, "1.6"));
    }

    [Fact]
    public void InspectorInputsAreEditableAndKeepFocusAcrossRebuild()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // The float editors render as real text inputs, not read-only labels.
        var metallic = FindInput(content, "0.15");
        Assert.NotNull(metallic);

        // Focus the Metallic field, then replace its value: the @onchange
        // handler queues the write for the host and re-renders the page.
        ui.ProcessPointerDown(metallic!.Layout.X + 1, metallic.Layout.Y + 1);
        ui.ProcessPointerUp(metallic.Layout.X + 1, metallic.Layout.Y + 1);
        metallic.SetValue("0.25");
        ui.Update();
        ui.Prepare();

        // The edit is queued under the stable write-back key.
        var edits = EditorInspectorState.ConsumeEdits();
        Assert.Contains(edits, edit => edit.Key == "MeshRenderer.Material.metallic" && edit.Value == "0.25");

        // The rebuild keeps the edited input's value and focus (it must not
        // jump to the first input on the page).
        var rerendered = FindInput(ui.Content!, "0.25");
        Assert.NotNull(rerendered);
        Assert.True(rerendered!.IsFocused);
        Assert.Same(rerendered, ui.FocusedPanel);
    }

    [Fact]
    public void ViewportToolbarIsHorizontallyCentered()
    {
        using var ui = CreateEditorUi();

        var toolbar = TestUi.Find(ui.Content!, p => p.Classes.Contains("viewport-toolbar"));
        Assert.NotNull(toolbar);
        var viewport = toolbar!.Parent;
        Assert.NotNull(viewport);

        var toolbarCenter = toolbar.Layout.X + toolbar.Layout.Width / 2;
        var viewportCenter = viewport!.Layout.X + viewport.Layout.Width / 2;
        Assert.True(MathF.Abs(toolbarCenter - viewportCenter) < 1f,
            $"toolbar center {toolbarCenter} is not the viewport center {viewportCenter}");
    }

    [Fact]
    public void NotificationPage_CompilesAndRendersTheSingleLatestNotification()
    {
        // The notifications this test pushes are process-global and would leak
        // into the next test (a leftover toast overlays the page and swallows
        // clicks), so they are cleared on the way out.
        try
        {
            using var ui = CreateEditorUi();
            ui.Navigate("/notifications");
            ui.Update();
            ui.Prepare();

            // The notification-window page compiled: before anything is pushed the
            // popup is empty (no header, no feed, no empty state — nothing at all).
            Assert.Equal("/notifications", ui.CurrentUrl);
            Assert.NotNull(ui.Content);
            Assert.Empty(TestUi.FindAll(ui.Content!, p => p.Classes.Contains("notify")));

            // A success compilation result renders as the single popup.
            UiNotifications.Show("Hot reload", "Full reload: 3 instance(s) migrated", "success");
            ui.Update();
            ui.Prepare();

            var popup = TestUi.Find(ui.Content!, p => p.Classes.Contains("notify") && p.Classes.Contains("success"));
            Assert.NotNull(popup);
            Assert.Contains(TestUi.Texts(popup!), t => t.Contains("Full reload: 3 instance(s)", StringComparison.Ordinal));

            // A newer notification replaces the previous one: still a single popup,
            // now the error — the window never shows several notifications at once.
            UiNotifications.Show("Hot reload", "Failed: compilation error", "error");
            ui.Update();
            ui.Prepare();

            var popups = TestUi.FindAll(ui.Content!, p => p.Classes.Contains("notify"));
            Assert.Single(popups);
            Assert.Contains("error", popups[0].Classes);
            Assert.Contains(TestUi.Texts(popups[0]), t => t.Contains("Failed: compilation error", StringComparison.Ordinal));
        }
        finally
        {
            UiNotifications.Reset();
        }
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
