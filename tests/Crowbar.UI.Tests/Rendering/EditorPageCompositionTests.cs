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
    /// (a level at the root, models and textures under Models/, a sound under
    /// Sounds/), so the Content panel asserts see the tiles the host would
    /// publish from the live content folder.
    /// </summary>
    internal static void PublishDemoContentState()
    {
        EditorContentState.Publish(
        [
            new EditorContentState.Entry("Content/Demo.level", "Demo.level", "file"),
            new EditorContentState.Entry("Content/Models/Crate/Crate.gltf", "Crate.gltf", "model"),
            new EditorContentState.Entry("Content/Models/Crate/Crate_basecolor.png", "Crate_basecolor.png", "texture"),
            new EditorContentState.Entry("Content/Models/industrial_work_light/industrial_work_light.gltf", "industrial_work_light.gltf", "model"),
            new EditorContentState.Entry("Content/Sounds/ui_compilation_error.wav", "ui_compilation_error.wav", "sound")
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

    /// <summary>The caret (expand/collapse arrow) icon of a content tree row, or null when the row has no children.</summary>
    private static Icon? CaretOf(Panel row) =>
        TestUi.FindAll(row, p => p is Icon i && i.Name is not null &&
            (i.Name.Contains("alt-arrow-down", StringComparison.Ordinal) ||
             i.Name.Contains("alt-arrow-right", StringComparison.Ordinal)))
            .Cast<Icon>().SingleOrDefault();

    /// <summary>Pointer-down/up at the panel's top-left corner (inside its padding for rows, inside the box for icons).</summary>
    private static void ClickAt(UiSystem ui, Panel panel)
    {
        ui.ProcessPointerDown(panel.Layout.X + 2, panel.Layout.Y + 2);
        ui.ProcessPointerUp(panel.Layout.X + 2, panel.Layout.Y + 2);
        ui.Update();
        ui.Prepare();
    }

    /// <summary>
    /// A point inside the content grid that no tile occupies (the grid's
    /// top-right corner): tiles flow from the top-left, so the right edge
    /// stays empty whatever folder is browsed. The sidebar is avoided because
    /// its tree rows can fill the whole strip.
    /// </summary>
    private static (float X, float Y) ContentEmptyPoint(Panel content)
    {
        var grid = TestUi.Find(content, p => p.Classes.Contains("content-grid"));
        Assert.NotNull(grid);
        return (grid!.Layout.Right - 25, grid.Layout.Y + 25);
    }

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
        var selectedRow = TestUi.Find(content, p => p.Classes.Contains("ctree-selected"));
        Assert.NotNull(selectedRow);
        Assert.Contains("Content", TestUi.Texts(selectedRow!));
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

        // At the content root the grid lists the top-level folders plus root
        // files (folders and files both use the project's icon pack).
        Assert.NotNull(FindText(content, "asset-name", t => t == "Models"));
        Assert.NotNull(FindText(content, "asset-name", t => t == "Sounds"));
        Assert.NotNull(FindText(content, "asset-name", t => t == "Demo.level"));

        // The sidebar is a folder tree: the root plus one row per top-level
        // folder (root files are not folders and stay in the grid).
        var sidebar = TestUi.FindAll(content, p => p.Classes.Contains("ctree-row"))
            .SelectMany(TestUi.Texts)
            .ToArray();
        Assert.Contains("Content", sidebar);
        Assert.Contains("Models", sidebar);
        Assert.Contains("Sounds", sidebar);
        Assert.DoesNotContain("Demo.level", sidebar);

        // The breadcrumb shows the content root, the folder tiles carry the
        // project's folder icon and the root file its generic file icon.
        Assert.NotNull(FindText(content, "crumb", t => t == "Content"));
        var icons = IconsOf(content);
        Assert.Contains("Solar/folders/Bold/folder-2", icons);
        Assert.Contains("Solar/files/Bold/file", icons);
    }

    [Fact]
    public void ContentPanelBrowsesIntoFoldersAndBack()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // Sidebar tree: clicking Models browses into it and shows its folders
        // (no files at that level).
        var models = FindText(content, "ctree-row", t => t == "Models");
        Assert.NotNull(models);
        ui.ProcessPointerDown(models!.Layout.X + 2, models.Layout.Y + 2);
        ui.ProcessPointerUp(models.Layout.X + 2, models.Layout.Y + 2);
        ui.Update();
        ui.Prepare();

        content = ui.Content!;
        Assert.NotNull(FindText(content, "asset-name", t => t == "Crate"));
        Assert.NotNull(FindText(content, "asset-name", t => t == "industrial_work_light"));
        Assert.Null(FindText(content, "asset-name", t => t == "Crate.gltf"));
        Assert.NotNull(FindText(content, "crumb", t => t == "Models"));

        // Folder tile: clicking Crate browses into it and shows its files,
        // each tile carrying the icon of its registered asset type.
        var crate = FindText(content, "asset-name", t => t == "Crate");
        Assert.NotNull(crate);
        ui.ProcessPointerDown(crate!.Layout.X + 2, crate.Layout.Y + 2);
        ui.ProcessPointerUp(crate.Layout.X + 2, crate.Layout.Y + 2);
        ui.Update();
        ui.Prepare();

        content = ui.Content!;
        Assert.NotNull(FindText(content, "asset-name", t => t == "Crate.gltf"));
        Assert.NotNull(FindText(content, "asset-name", t => t == "Crate_basecolor.png"));
        Assert.Null(FindText(content, "asset-name", t => t == "industrial_work_light"));
        Assert.NotNull(FindText(content, "crumb", t => t == "Crate"));
        var icons = IconsOf(content);
        Assert.Contains("cube", icons);
        var textureThumbnail = TestUi.Find(content,
            panel => panel is Image image && image.Source == "Content/Models/Crate/Crate_basecolor.png");
        Assert.NotNull(textureThumbnail);

        // Breadcrumb: clicking the Models crumb navigates back up.
        var modelsCrumb = FindText(content, "crumb", t => t == "Models");
        Assert.NotNull(modelsCrumb);
        ui.ProcessPointerDown(modelsCrumb!.Layout.X + 2, modelsCrumb.Layout.Y + 2);
        ui.ProcessPointerUp(modelsCrumb.Layout.X + 2, modelsCrumb.Layout.Y + 2);
        ui.Update();
        ui.Prepare();

        content = ui.Content!;
        Assert.NotNull(FindText(content, "asset-name", t => t == "Crate"));
        Assert.Null(FindText(content, "asset-name", t => t == "Crate.gltf"));
    }

    [Fact]
    public void ContentContextMenuClosesWhenClickingOutsideWithLeftButton()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;
        var asset = FindText(content, "asset-name", t => t == "Demo.level");
        Assert.NotNull(asset);

        // Open the menu with the secondary button on a content tile.
        ui.ProcessPointerDown(asset!.Layout.X + 2, asset.Layout.Y + 2, button: 1);
        ui.ProcessPointerUp(asset.Layout.X + 2, asset.Layout.Y + 2, button: 1);
        ui.Update();
        ui.Prepare();
        Assert.NotNull(TestUi.Find(ui.Content, p => p.Classes.Contains("content-context-menu")));

        // The transparent full-editor layer receives left clicks outside the
        // menu, while the menu itself consumes clicks so editing remains safe.
        ui.ProcessPointerDown(5, 5, button: 0);
        ui.ProcessPointerUp(5, 5, button: 0);
        ui.Update();
        ui.Prepare();

        Assert.Null(TestUi.Find(ui.Content, p => p.Classes.Contains("content-context-menu")));
    }

    [Fact]
    public void ContentEmptyAreaRightClickOpensNewItemMenu()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // Right-click an empty spot of the content body (the sidebar below
        // the tree rows): the empty-area menu opens with the new-item actions.
        var (x, y) = ContentEmptyPoint(content);
        ui.ProcessPointerDown(x, y, button: 1);
        ui.ProcessPointerUp(x, y, button: 1);
        ui.Update();
        ui.Prepare();

        content = ui.Content!;
        var menu = TestUi.Find(content, p => p.Classes.Contains("content-context-menu"));
        Assert.NotNull(menu);
        // The title is the browsed folder (the content root here), the actions
        // create items inside it — no item actions (Open/Rename/...).
        Assert.Contains("Content", TestUi.Texts(menu!));
        Assert.NotNull(FindText(content, "context-menu-item", t => t == "New Folder"));
        Assert.NotNull(FindText(content, "context-menu-item", t => t == "New File"));
        Assert.NotNull(FindText(content, "context-menu-item", t => t == "Import..."));
        Assert.NotNull(FindText(content, "context-menu-item", t => t == "Refresh"));
        Assert.Null(FindText(content, "context-menu-item", t => t == "Rename"));
    }

    [Fact]
    public void ContentEmptyAreaMenuCreatesFolderWithInlineName()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // Browse into Models so the empty-area menu targets the browsed folder.
        var models = FindText(content, "ctree-row", t => t == "Models");
        Assert.NotNull(models);
        ClickAt(ui, models!);
        content = ui.Content!;

        // Right-click an empty spot and pick "New Folder": the menu switches
        // to an inline name input prefilled with the default.
        var (x, y) = ContentEmptyPoint(content);
        ui.ProcessPointerDown(x, y, button: 1);
        ui.ProcessPointerUp(x, y, button: 1);
        ui.Update();
        ui.Prepare();

        var newFolder = FindText(ui.Content!, "context-menu-item", t => t == "New Folder");
        Assert.NotNull(newFolder);
        ClickAt(ui, newFolder!);

        var input = TestUi.Find(ui.Content!, p => p is TextInput && p.Classes.Contains("context-rename-input"));
        Assert.NotNull(input);
        Assert.Equal("New Folder", ((TextInput)input!).Value);

        // Type a name and confirm: the create request is queued for the host
        // and the menu closes.
        ((TextInput)input).SetValue("MyFolder");
        ui.Update();
        ui.Prepare();
        var save = FindText(ui.Content!, "context-menu-item", t => t == "Save");
        Assert.NotNull(save);
        ClickAt(ui, save!);

        Assert.True(EditorContentState.TryConsumeAction(out var action));
        Assert.Equal(EditorContentState.ActionKind.CreateFolder, action.Kind);
        Assert.Equal("Models", action.Path);
        Assert.Equal("MyFolder", action.Value);
        Assert.Null(TestUi.Find(ui.Content!, p => p.Classes.Contains("content-context-menu")));
    }

    [Fact]
    public void ContentEmptyAreaMenuCreatesFileWithDefaultName()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // Right-click an empty spot and pick "New File": the input is
        // prefilled with a default name that is created untouched on Save.
        var (x, y) = ContentEmptyPoint(content);
        ui.ProcessPointerDown(x, y, button: 1);
        ui.ProcessPointerUp(x, y, button: 1);
        ui.Update();
        ui.Prepare();

        var newFile = FindText(ui.Content!, "context-menu-item", t => t == "New File");
        Assert.NotNull(newFile);
        ClickAt(ui, newFile!);

        var input = TestUi.Find(ui.Content!, p => p is TextInput && p.Classes.Contains("context-rename-input"));
        Assert.NotNull(input);
        Assert.Equal("New File.txt", ((TextInput)input!).Value);

        var save = FindText(ui.Content!, "context-menu-item", t => t == "Save");
        Assert.NotNull(save);
        ClickAt(ui, save!);

        Assert.True(EditorContentState.TryConsumeAction(out var action));
        Assert.Equal(EditorContentState.ActionKind.CreateFile, action.Kind);
        Assert.Equal(string.Empty, action.Path);
        Assert.Equal("New File.txt", action.Value);
        Assert.Null(TestUi.Find(ui.Content!, p => p.Classes.Contains("content-context-menu")));
    }

    [Fact]
    public void ContentContextMenuStaysWithinTheWindowOnFirstRightClick()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // Right-click an empty spot near the bottom of the content grid: the
        // first open must clamp the menu fully inside the window, exactly like
        // subsequent opens (the first render used to fall back to a hard-coded
        // 1920x1080 viewport, letting the menu overflow a smaller window).
        var grid = TestUi.Find(content, p => p.Classes.Contains("content-grid"));
        Assert.NotNull(grid);
        var x = grid!.Layout.Right - 15;
        var y = grid.Layout.Bottom - 12;
        ui.ProcessPointerDown(x, y, button: 1);
        ui.ProcessPointerUp(x, y, button: 1);
        // The first frame mounts the menu while the root rebuild is still in
        // progress, before it has a laid-out parent: it renders hidden at the
        // press position and schedules a follow-up render. The second frame
        // clamps it into the window — assert on that frame, like a real user
        // sees the menu.
        ui.Update();
        ui.Prepare();
        ui.Update();
        ui.Prepare();

        var menu = TestUi.Find(ui.Content!, p => p.Classes.Contains("content-context-menu"));
        Assert.NotNull(menu);
        var viewport = ui.Screen.Layout;
        Assert.True(menu!.Layout.X >= 4, $"menu left {menu.Layout.X} must be >= 4");
        Assert.True(menu.Layout.Y >= 4, $"menu top {menu.Layout.Y} must be >= 4");
        Assert.True(menu.Layout.Right <= viewport.Width - 4,
            $"menu right {menu.Layout.Right} must fit width {viewport.Width}");
        Assert.True(menu.Layout.Bottom <= viewport.Height - 4,
            $"menu bottom {menu.Layout.Bottom} must fit height {viewport.Height}");
    }

    [Fact]
    public void ContentContextMenuRepositionsOnSubsequentRightClick()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // Open the empty-area menu at one spot of the content grid (top-right
        // stays empty: tiles flow from the top-left).
        var grid = TestUi.Find(content, p => p.Classes.Contains("content-grid"));
        Assert.NotNull(grid);
        var p1 = (grid!.Layout.Right - 25, grid.Layout.Y + 30);
        ui.ProcessPointerDown(p1.Item1, p1.Item2, button: 1);
        ui.ProcessPointerUp(p1.Item1, p1.Item2, button: 1);
        ui.Update();
        ui.Prepare();
        ui.Update();
        ui.Prepare();

        var menu = TestUi.Find(ui.Content!, p => p.Classes.Contains("content-context-menu"));
        Assert.NotNull(menu);
        var firstX = menu!.Layout.X;
        var firstY = menu.Layout.Y;

        // Right-click again on the sidebar's empty area below the tree rows
        // (far from the open menu and from every tile): the menu must move
        // there instead of staying where it was.
        var sidebar = TestUi.Find(content, p => p.Classes.Contains("content-sidebar"));
        Assert.NotNull(sidebar);
        var px = sidebar!.Layout.X + 60;
        var py = sidebar.Layout.Bottom - 25;
        Assert.True(Math.Abs(px - firstX) > 1f, "second press must be a different spot");
        ui.ProcessPointerDown(px, py, button: 1);
        ui.ProcessPointerUp(px, py, button: 1);
        ui.Update();
        ui.Prepare();
        ui.Update();
        ui.Prepare();

        menu = TestUi.Find(ui.Content!, p => p.Classes.Contains("content-context-menu"));
        Assert.NotNull(menu);
        // The menu moved from the grid to the sidebar (a clear lateral shift;
        // the vertical stays clamped to the window bottom for both spots).
        Assert.True(menu!.Layout.X < firstX - 100f,
            $"menu left {menu.Layout.X} must move left of first open {firstX}");
        // Still fully inside the window.
        var viewport = ui.Screen.Layout;
        Assert.True(menu.Layout.Right <= viewport.Width - 4,
            $"menu right {menu.Layout.Right} must fit width {viewport.Width}");
        Assert.True(menu.Layout.Bottom <= viewport.Height - 4,
            $"menu bottom {menu.Layout.Bottom} must fit height {viewport.Height}");
    }

    [Fact]
    public void ContentEmptyFolderAppearsInGridAndTree()
    {
        using var ui = CreateEditorUi();
        // A brand-new empty folder: the host publishes it as a "folder" entry
        // (there are no files under it yet), and it must still appear in the
        // grid and the sidebar tree exactly like a file-backed folder.
        EditorContentState.Publish(
        [
            new EditorContentState.Entry("Content/NewFolder", "NewFolder", "folder"),
            new EditorContentState.Entry("Content/Demo.level", "Demo.level", "file")
        ]);
        ui.Update();
        ui.Prepare();
        var content = ui.Content!;

        Assert.NotNull(FindText(content, "asset-name", t => t == "NewFolder"));
        Assert.NotNull(FindText(content, "ctree-row", t => t == "NewFolder"));
        Assert.NotNull(FindText(content, "asset-name", t => t == "Demo.level"));
    }

    [Fact]
    public void RightClickOnContentTileKeepsTheItemMenu()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;
        var asset = FindText(content, "asset-name", t => t == "Demo.level");
        Assert.NotNull(asset);

        // Right-clicking a tile opens the item menu (Open/Rename/Delete), not
        // the empty-area menu, even though the press bubbles through the body.
        ui.ProcessPointerDown(asset!.Layout.X + 2, asset.Layout.Y + 2, button: 1);
        ui.ProcessPointerUp(asset.Layout.X + 2, asset.Layout.Y + 2, button: 1);
        ui.Update();
        ui.Prepare();

        content = ui.Content!;
        var menu = TestUi.Find(content, p => p.Classes.Contains("content-context-menu"));
        Assert.NotNull(menu);
        Assert.Contains("Demo.level", TestUi.Texts(menu!));
        Assert.NotNull(FindText(content, "context-menu-item", t => t == "Rename"));
        Assert.Null(FindText(content, "context-menu-item", t => t == "New Folder"));
    }

    [Fact]
    public void ContextMenuInputSupportsMouseAndKeyboardSelection()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;
        var asset = FindText(content, "asset-name", t => t == "Demo.level");
        Assert.NotNull(asset);

        // Open the item menu and switch it to the inline rename input.
        ui.ProcessPointerDown(asset!.Layout.X + 2, asset.Layout.Y + 2, button: 1);
        ui.ProcessPointerUp(asset.Layout.X + 2, asset.Layout.Y + 2, button: 1);
        ui.Update();
        ui.Prepare();
        ui.Update();
        ui.Prepare();
        var rename = FindText(ui.Content!, "context-menu-item", t => t == "Rename");
        Assert.NotNull(rename);
        ClickAt(ui, rename!);

        // Click the input to focus it and park the caret at the start.
        var input = TestUi.Find(ui.Content!, p => p is TextInput && p.Classes.Contains("context-rename-input"));
        Assert.NotNull(input);
        var textInput = (TextInput)input!;
        Assert.Equal("Demo.level", textInput.Value);
        ui.ProcessPointerDown(textInput.Layout.X + 3, textInput.Layout.Y + 3);
        ui.ProcessPointerUp(textInput.Layout.X + 3, textInput.Layout.Y + 3);
        ui.Update();
        ui.Prepare();

        // Ctrl+A selects the whole value (no rebuild may interrupt the key
        // sequence, or the control state would be lost between the keys).
        ui.ProcessKey(0x11, isDown: true);
        ui.ProcessKey(0x41, isDown: true);
        ui.ProcessKey(0x41, isDown: false);
        ui.ProcessKey(0x11, isDown: false);
        ui.Update();
        ui.Prepare();
        input = TestUi.Find(ui.Content!, p => p is TextInput && p.Classes.Contains("context-rename-input"));
        Assert.NotNull(input);
        textInput = (TextInput)input;
        Assert.True(textInput.HasSelection, "Ctrl+A must select the whole value");
        Assert.Equal(0, textInput.SelectionStart);
        Assert.Equal("Demo.level".Length, textInput.SelectionEnd);

        // Shift+arrow extends the selection from the caret: a plain arrow
        // collapses it, then Shift+Left re-selects the last character.
        ui.ProcessKey(0x27, isDown: true);
        ui.ProcessKey(0x27, isDown: false);
        ui.ProcessKey(0x10, isDown: true);
        ui.ProcessKey(0x25, isDown: true);
        ui.ProcessKey(0x25, isDown: false);
        ui.ProcessKey(0x10, isDown: false);
        ui.Update();
        ui.Prepare();
        input = TestUi.Find(ui.Content!, p => p is TextInput && p.Classes.Contains("context-rename-input"));
        Assert.NotNull(input);
        textInput = (TextInput)input;
        Assert.True(textInput.HasSelection, "Shift+arrow must extend the selection");
        // Shift+Left anchors at the caret (10) and moves the other end to 9:
        // the selection covers exactly the last character.
        Assert.Equal("Demo.level".Length, Math.Max(textInput.SelectionStart, textInput.SelectionEnd));
        Assert.Equal("Demo.level".Length - 1, Math.Min(textInput.SelectionStart, textInput.SelectionEnd));

        // Mouse drag selects a range: press, move, release.
        ui.ProcessPointerDown(textInput.Layout.X + 3, textInput.Layout.Y + 3);
        ui.ProcessPointerMove(textInput.Layout.X + 70, textInput.Layout.Y + 3);
        ui.ProcessPointerUp(textInput.Layout.X + 70, textInput.Layout.Y + 3);
        ui.Update();
        ui.Prepare();
        input = TestUi.Find(ui.Content!, p => p is TextInput && p.Classes.Contains("context-rename-input"));
        Assert.NotNull(input);
        textInput = (TextInput)input;
        Assert.True(textInput.HasSelection, "mouse drag must select text");
        Assert.True(textInput.SelectionStart < textInput.SelectionEnd);
    }

    [Fact]
    public void ContentPanelSidebarIsAFolderTree()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // Expanding Models reveals its folders, indented under it.
        var models = FindText(content, "ctree-row", t => t == "Models");
        Assert.NotNull(models);
        ui.ProcessPointerDown(models!.Layout.X + 2, models.Layout.Y + 2);
        ui.ProcessPointerUp(models.Layout.X + 2, models.Layout.Y + 2);
        ui.Update();
        ui.Prepare();

        content = ui.Content!;
        Assert.NotNull(FindText(content, "ctree-row", t => t == "Crate"));
        Assert.NotNull(FindText(content, "ctree-row", t => t == "industrial_work_light"));

        // Clicking a tree row navigates the grid and keeps the path open.
        var crate = FindText(content, "ctree-row", t => t == "Crate");
        Assert.NotNull(crate);
        ui.ProcessPointerDown(crate!.Layout.X + 2, crate.Layout.Y + 2);
        ui.ProcessPointerUp(crate.Layout.X + 2, crate.Layout.Y + 2);
        ui.Update();
        ui.Prepare();

        content = ui.Content!;
        Assert.NotNull(FindText(content, "asset-name", t => t == "Crate.gltf"));
        Assert.NotNull(FindText(content, "crumb", t => t == "Crate"));
        // Models stays open: it is an ancestor of the browsed folder.
        Assert.NotNull(FindText(content, "ctree-row", t => t == "industrial_work_light"));
    }

    [Fact]
    public void ContentTreeCaretFoldsAndUnfoldsTheSubtreeWithoutNavigating()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // Browse into Models via its label (which also reveals the subtree).
        var models = FindText(content, "ctree-row", t => t == "Models");
        Assert.NotNull(models);
        ClickAt(ui, models!);
        content = ui.Content!;
        Assert.NotNull(FindText(content, "ctree-row", t => t == "Crate"));

        // Clicking the caret folds the subtree without navigating: the grid
        // still shows the Models folders and the caret flips to the right.
        models = FindText(content, "ctree-row", t => t == "Models");
        Assert.NotNull(models);
        var caret = CaretOf(models!);
        Assert.NotNull(caret);
        Assert.EndsWith("alt-arrow-down", caret!.Name!, StringComparison.Ordinal);
        ClickAt(ui, caret);
        content = ui.Content!;

        Assert.Null(FindText(content, "ctree-row", t => t == "Crate"));
        Assert.NotNull(FindText(content, "asset-name", t => t == "Crate"));
        Assert.NotNull(FindText(content, "asset-name", t => t == "industrial_work_light"));
        models = FindText(content, "ctree-row", t => t == "Models");
        Assert.NotNull(models);
        Assert.EndsWith("alt-arrow-right", CaretOf(models!)!.Name!, StringComparison.Ordinal);

        // The same caret reopens the subtree.
        ClickAt(ui, CaretOf(models!)!);
        content = ui.Content!;
        Assert.NotNull(FindText(content, "ctree-row", t => t == "Crate"));
        Assert.EndsWith("alt-arrow-down", CaretOf(FindText(content, "ctree-row", t => t == "Models")!)!.Name!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ContentTreeFoldsTheBrowsedFolder()
    {
        using var ui = CreateEditorUi();
        var content = ui.Content!;

        // Browse into Models so it is the selected/current folder.
        var models = FindText(content, "ctree-row", t => t == "Models");
        Assert.NotNull(models);
        ClickAt(ui, models!);
        content = ui.Content!;
        Assert.NotNull(FindText(content, "asset-name", t => t == "Crate"));

        // The browsed folder itself can be folded: its caret collapses the
        // subtree, the row stays visible and selected, and the grid (which
        // navigated independently) keeps showing Models' content.
        models = FindText(content, "ctree-row", t => t == "Models");
        Assert.NotNull(models);
        Assert.True(models!.Classes.Contains("ctree-selected"));
        ClickAt(ui, CaretOf(models)!);
        content = ui.Content!;

        Assert.Null(FindText(content, "ctree-row", t => t == "Crate"));
        Assert.NotNull(FindText(content, "asset-name", t => t == "Crate"));
        models = FindText(content, "ctree-row", t => t == "Models");
        Assert.NotNull(models);
        Assert.True(models!.Classes.Contains("ctree-selected"));

        // Clicking the folded row's label reopens it (navigation reveals).
        ClickAt(ui, models);
        content = ui.Content!;
        Assert.NotNull(FindText(content, "ctree-row", t => t == "Crate"));
    }

    [Fact]
    public void ContentTreeRowsAlignIconsByDepth()
    {
        using var ui = CreateEditorUi();
        // Publish a deeper hierarchy (textures under industrial_work_light)
        // and browse into the nested folder, mirroring the demo project.
        EditorContentState.Publish(
        [
            new EditorContentState.Entry("Content/Models/Crate/Crate.gltf", "Crate.gltf", "model"),
            new EditorContentState.Entry("Content/Models/industrial_work_light/industrial_work_light.gltf", "industrial_work_light.gltf", "model"),
            new EditorContentState.Entry("Content/Models/industrial_work_light/textures/Industrial_Light_baseColor.png", "Industrial_Light_baseColor.png", "texture")
        ]);
        EditorContentState.NavigateTo("Models/industrial_work_light");
        ui.Update();
        ui.Prepare();

        var content = ui.Content!;
        var models = FindText(content, "ctree-row", t => t == "Models");
        var crate = FindText(content, "ctree-row", t => t == "Crate");
        var industrial = FindText(content, "ctree-row", t => t == "industrial_work_light");
        var textures = FindText(content, "ctree-row", t => t == "textures");
        Assert.NotNull(models);
        Assert.NotNull(crate);
        Assert.NotNull(industrial);
        Assert.NotNull(textures);

        // The folder icon of each row: the caret column (12px + 4px gap) is
        // reserved on every row, so icons align across siblings and step one
        // level per depth — Crate sits clearly under Models, not at its level.
        static float FolderX(Panel row) =>
            TestUi.FindAll(row, p => p is Icon i && i.Name == "Solar/folders/Bold/folder-2")
                .Cast<Icon>().Single().Layout.X;

        Assert.Equal(FolderX(crate!), FolderX(industrial!));
        Assert.True(FolderX(models!) < FolderX(crate!), "Models must sit one level above Crate");
        Assert.True(FolderX(industrial!) < FolderX(textures!), "textures must sit one level below industrial_work_light");

        // Labels align the same way (Crate has no caret, industrial does).
        static float TextX(Panel row) =>
            TestUi.FindAll(row, p => p.Classes.Contains("ctree-text")).Single().Layout.X;
        Assert.Equal(TextX(crate!), TextX(industrial!));
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
