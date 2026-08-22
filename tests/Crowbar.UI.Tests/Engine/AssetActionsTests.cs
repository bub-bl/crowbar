using Crowbar.Engine.Global;

namespace Crowbar.Engine.Tests;

/// <summary>
/// Tests for the context-action registry (<see cref="AssetActions"/>): static
/// methods marked <see cref="AssetActionAttribute"/> are discovered per
/// assembly, filtered by extension (or a registered type's extensions) for a
/// path, executed with a context and a notification callback, and dropped on
/// <see cref="AssetActions.Unregister"/> like the resource library. This is
/// the API game code uses to contribute context-menu actions to the editor's
/// content panel.
/// </summary>
public class AssetActionsTests
{
    /// <summary>Actions declared exactly like a game project would declare them.</summary>
    private static class DeclaredActions
    {
        /// <summary>Filtered by explicit extensions; reports through the context's notification callback.</summary>
        [AssetAction("convert", "Convert to Splat", Extensions = new[] { "png", "jpg" })]
        public static void Convert(AssetActionContext ctx)
        {
            Invocations.Add($"convert:{ctx.Path}");
            ctx.Notify?.Invoke("Content", "Converted!", "success");
        }

        /// <summary>Filtered by a registered asset type: its [AssetType] extensions decide the match.</summary>
        [AssetAction("bake", "Bake Lightmap", AssetType = typeof(Model))]
        public static void Bake(AssetActionContext ctx) => Invocations.Add($"bake:{ctx.Path}");

        /// <summary>Destructive, ordered last, applies to every file (no filter).</summary>
        [AssetAction("danger", "Delete Source", IsDanger = true, Order = 1)]
        public static void Danger(AssetActionContext ctx) => Invocations.Add($"danger:{ctx.Path}");

        /// <summary>No filter: applies to every file, default order.</summary>
        [AssetAction("universal", "Universal")]
        public static void Universal(AssetActionContext ctx) => Invocations.Add($"universal:{ctx.Path}");
    }

    private static readonly List<string> Invocations = [];

    /// <summary>Registers the test assembly's declared actions (idempotent across tests) and clears the invocation log.</summary>
    private static void RegisterActions()
    {
        Invocations.Clear();
        AssetActions.Register(typeof(DeclaredActions).Assembly);
    }

    [Fact]
    public void ForPath_FiltersByDeclaredExtensions()
    {
        RegisterActions();

        // convert matches png; bake (Model) and danger/universal (no filter)
        // decide separately — ordered by Order then id.
        var ids = AssetActions.ForPath("Content/Models/Crate/Crate.png").Select(action => action.Id).ToArray();
        Assert.Equal(new[] { "convert", "universal", "danger" }, ids);

        // jpg matches convert too; an unknown extension only keeps the
        // filterless actions.
        Assert.Contains(AssetActions.ForPath("Content/a.jpg"), action => action.Id == "convert");
        var unknown = AssetActions.ForPath("Content/a.xyz").Select(action => action.Id).ToArray();
        Assert.Equal(new[] { "universal", "danger" }, unknown);
    }

    [Fact]
    public void ForPath_FiltersByRegisteredAssetType()
    {
        RegisterActions();

        // bake is declared for AssetType = Model, whose [AssetType] extensions
        // are gltf/obj/fbx: a .gltf file matches it.
        var gltf = AssetActions.ForPath("Content/Models/Crate/Crate.gltf").Select(action => action.Id).ToArray();
        Assert.Equal(new[] { "bake", "universal", "danger" }, gltf);

        // A .level file (a registered type, but not Model) does not match.
        Assert.DoesNotContain(AssetActions.ForPath("Content/Demo.level"), action => action.Id == "bake");
    }

    [Fact]
    public void ForPath_CarriesTheLabelAndDangerFlag()
    {
        RegisterActions();

        var danger = Assert.Single(AssetActions.ForPath("Content/a.png"), action => action.Id == "danger");
        Assert.Equal("Delete Source", danger.Label);
        Assert.True(danger.IsDanger);
        var convert = Assert.Single(AssetActions.ForPath("Content/a.png"), action => action.Id == "convert");
        Assert.False(convert.IsDanger);
    }

    [Fact]
    public void Execute_RunsTheHandlerWithTheContextAndNotification()
    {
        RegisterActions();
        string? notifyMessage = null;

        Assert.True(AssetActions.Execute("convert", "Content/a.png", (title, message, kind) => notifyMessage = message));

        Assert.Equal(new[] { "convert:Content/a.png" }, Invocations);
        Assert.Equal("Converted!", notifyMessage);
    }

    [Fact]
    public void Execute_UnknownId_ReturnsFalse()
    {
        RegisterActions();

        Assert.False(AssetActions.Execute("missing", "Content/a.png"));
        Assert.Empty(Invocations);
    }

    [Fact]
    public void Register_IsIdempotent()
    {
        RegisterActions();

        // Like ResourceLibrary: registering the same assembly again must not
        // reset or duplicate the actions.
        AssetActions.Register(typeof(DeclaredActions).Assembly);

        Assert.Single(AssetActions.ForPath("Content/a.png"), action => action.Id == "convert");
    }

    [Fact]
    public void Unregister_DropsTheAssemblysActions()
    {
        RegisterActions();
        Assert.NotEmpty(AssetActions.ForPath("Content/a.png"));

        AssetActions.Unregister(typeof(DeclaredActions).Assembly);

        // The actions no longer resolve: an id from the dropped assembly
        // (e.g. after a hot reload) is an unknown action, not a stale handler.
        Assert.Empty(AssetActions.ForPath("Content/a.png"));
        Assert.False(AssetActions.Execute("convert", "Content/a.png"));
    }
}
