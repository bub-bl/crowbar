using Crowbar.Editor;
using Crowbar.Engine;
using Crowbar.Engine.Scripting;

namespace Crowbar.UI.Tests.Scripting;

/// <summary>
/// The Editor.StatusBar API is the editor's public surface for game code: a
/// game project's components register entries when attached, the editor renders
/// whatever exists. These tests pin that contract and the component registry
/// that lets the editor attach game components by name.
/// </summary>
[Collection("EditorGlobals")]
public sealed class StatusBarTests
{
    public StatusBarTests()
    {
        StatusBar.Clear();
    }

    [Fact]
    public void AddEntrySnapshotsLiveProducerValues()
    {
        var score = 5;
        StatusBar.AddEntry("Game", () => $"{score} points");

        var snapshot = StatusBar.Snapshot();
        var entry = Assert.Single(snapshot);
        Assert.Equal("Game", entry.Label);
        Assert.Equal("5 points", entry.Value);

        score = 12; // producer is live
        Assert.Equal("12 points", Assert.Single(StatusBar.Snapshot()).Value);
    }

    [Fact]
    public void StringOverloadRemoveAndClear()
    {
        StatusBar.AddEntry("a", "1");
        StatusBar.AddEntry("b", "2");
        Assert.Equal(2, StatusBar.Snapshot().Count);

        StatusBar.AddEntry("a", "1bis"); // add-or-replace
        Assert.Equal("1bis", StatusBar.Snapshot().Single(e => e.Label == "a").Value);

        StatusBar.RemoveEntry("a");
        Assert.Equal("b", Assert.Single(StatusBar.Snapshot()).Label);

        StatusBar.Clear();
        Assert.Empty(StatusBar.Snapshot());
    }

    [Fact]
    public void ThrowingProducerShowsErrorPlaceholder()
    {
        StatusBar.AddEntry("Broken", () => throw new InvalidOperationException("boom"));
        Assert.Equal("(script error)", Assert.Single(StatusBar.Snapshot()).Value);
    }

    [Fact]
    public void TypeRegistryRegistersAndUnregistersAssemblyComponents()
    {
        using var dir = TestUi.TempDir("registry");
        dir.Write("Demo.cs", """
            using Crowbar.Engine;
            namespace Game;
            public sealed class DemoComponent : Component { }
            public abstract class AbstractComponent : Component { }
            public sealed class NoCtorComponent : Component { public NoCtorComponent(int x) { } }
            public sealed class NotAComponent { }
            """);

        using var assembly = new ScriptCompiler([typeof(ScriptHost).Assembly]).CompileDirectory(dir.Path, "RegistryGame");
        var engineOnlyCount = GlobalNamespaces.TypeLibrary.Registry.All.Count;

        // The editor registers the game project's assembly: concrete,
        // instantiable Component types become attachable by their short name.
        GlobalNamespaces.TypeLibrary.Registry.RegisterAssembly(assembly.Assembly);
        Assert.Contains(GlobalNamespaces.TypeLibrary.Registry.All, t => t.Name == "DemoComponent");
        Assert.Null(GlobalNamespaces.TypeLibrary.Registry.Resolve("AbstractComponent")); // abstract
        Assert.Null(GlobalNamespaces.TypeLibrary.Registry.Resolve("NoCtorComponent")); // no parameterless constructor
        Assert.Null(GlobalNamespaces.TypeLibrary.Registry.Resolve("NotAComponent")); // not a Component

        // A full reload unregisters the previous generation's types.
        GlobalNamespaces.TypeLibrary.Registry.UnregisterAssembly(assembly.Assembly);
        Assert.Equal(engineOnlyCount, GlobalNamespaces.TypeLibrary.Registry.All.Count);
        Assert.Null(GlobalNamespaces.TypeLibrary.Registry.Resolve("DemoComponent"));
    }

    [Fact]
    public void GameComponentRegistersStatusBarEntryWhenAttached()
    {
        using var dir = TestUi.TempDir("status");
        dir.Write("DemoComponent.cs", """
            using Crowbar.Engine;
            using Crowbar.Editor;
            namespace Game;
            public sealed class DemoComponent : Component
            {
                public int Score = 5;
                protected override void OnInitialize() => StatusBar.AddEntry("Game", () => $"{Score} points");
                protected override void OnDestroy() => StatusBar.RemoveEntry("Game");
            }
            """);

        using var assembly = new ScriptCompiler([typeof(ScriptHost).Assembly, typeof(StatusBar).Assembly])
            .CompileDirectory(dir.Path, "StatusGame");

        // The editor resolves the component by name through the registry — it
        // never references the game's types — then attaches an instance.
        GlobalNamespaces.TypeLibrary.Registry.RegisterAssembly(assembly.Assembly);
        var type = GlobalNamespaces.TypeLibrary.Registry.Resolve("DemoComponent");
        Assert.NotNull(type);

        using var world = new World();
        var entity = world.SpawnEntity("Hero");
        var component = (Component)Activator.CreateInstance(type)!;
        entity.AddComponent(component);

        // Attaching runs OnInitialize, which registers the live status entry.
        Assert.Equal("5 points", Assert.Single(StatusBar.Snapshot()).Value);

        // The producer stays live with the attached instance.
        type!.GetField("Score")!.SetValue(component, 7);
        Assert.Equal("7 points", Assert.Single(StatusBar.Snapshot()).Value);

        // Removing the component runs OnDestroy, which removes its entry.
        entity.RemoveComponent(component);
        Assert.Empty(StatusBar.Snapshot());
        GlobalNamespaces.TypeLibrary.Registry.UnregisterAssembly(assembly.Assembly);
    }
}