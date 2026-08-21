using Crowbar.Engine.Audio;
using Crowbar.Engine.Global;

namespace Crowbar.Engine.Tests;

/// <summary>
/// Tests for the cache API of <see cref="ResourceLibrary"/>: per-type caches
/// owned by one library instance, sharing by path, assembly registration
/// (<see cref="ResourceLibrary.Register"/>/<see cref="ResourceLibrary.Unregister"/>),
/// loader registration for custom resource types, and the invalidation/clear
/// lifecycle.
/// </summary>
public class ResourceLibraryTests
{
    /// <summary>A custom resource type with no engine knowledge, inheriting <see cref="ResourceFile"/>.</summary>
    [AssetType("custom")]
    private sealed class CustomResource : ResourceFile
    {
        public CustomResource(string path)
        {
            Path = path;
            IsValid = true;
        }
    }

    /// <summary>A resource that records when the cache disposes it on eviction.</summary>
    [AssetType("custom")]
    private sealed class TrackingResource : ResourceFile
    {
        public int UnloadCount { get; private set; }

        public override void Unload()
        {
            base.Unload();
            UnloadCount++;
        }
    }

    /// <summary>A <see cref="ResourceFile"/> subclass that deliberately skips the mandatory marker.</summary>
    private sealed class UnmarkedResource : ResourceFile
    {
    }

    /// <summary>A library with the engine's resource types registered (like Application.OnLoaded does).</summary>
    private static ResourceLibrary CreateLibrary()
    {
        var library = new ResourceLibrary();
        library.Register(typeof(Model).Assembly);
        return library;
    }

    [Fact]
    public void GetTypeForExtension_ResolvesAttributeDeclaredExtensions()
    {
        var library = CreateLibrary();

        Assert.Equal(typeof(Model), library.GetTypeForExtension("gltf"));
        Assert.Equal(typeof(Model), library.GetTypeForExtension(".GLTF")); // case-insensitive, leading dot optional
        Assert.Equal(typeof(Texture2D), library.GetTypeForExtension("jpeg"));
        Assert.Equal(typeof(AudioClip), library.GetTypeForExtension("ogg"));
        Assert.Equal(typeof(Shader), library.GetTypeForExtension("wgsl"));
        Assert.Equal(typeof(LevelFile), library.GetTypeForExtension("level"));
        Assert.Equal(typeof(CrowbarProjectFile), library.GetTypeForExtension("crproj"));
        Assert.Null(library.GetTypeForExtension("unknown"));
    }

    [Fact]
    public void GetTypeForExtension_IncludesRegisterLoaderTypes()
    {
        var library = new ResourceLibrary();
        library.RegisterLoader<CustomResource>(path => new CustomResource(path));

        Assert.Equal(typeof(CustomResource), library.GetTypeForExtension("custom"));
        Assert.Equal(typeof(CustomResource), library.Extensions["custom"]);
    }

    [Fact]
    public void Unregister_DropsTheAssemblysExtensions()
    {
        var library = new ResourceLibrary();
        library.RegisterLoader<CustomResource>(path => new CustomResource(path));
        Assert.Equal(typeof(CustomResource), library.GetTypeForExtension("custom"));

        library.Unregister(typeof(CustomResource).Assembly);

        // The extension must not keep resolving to a type from the unloaded
        // assembly (a hot-reload would otherwise return a stale Type).
        Assert.Null(library.GetTypeForExtension("custom"));
    }

    [Fact]
    public void Load_SharesTheSameInstanceForTheSamePath()
    {
        var library = CreateLibrary();

        var first = library.Load<Model>("Content/Models/Crate/Crate.gltf");
        var second = library.Load<Model>("Content/Models/Crate/Crate.gltf");

        Assert.Same(first, second);
        Assert.Equal(1, library.CachedCount<Model>());
    }

    [Fact]
    public void Load_AddressesThePathAsGiven_NoImplicitResolution()
    {
        var library = new ResourceLibrary();
        library.RegisterLoader<CustomResource>(path => new CustomResource(path));

        // Content addressing is explicit: the loader receives the exact path —
        // no implicit Content/ prefix and no engine fallback.
        Assert.Equal("widget.custom", library.Load<CustomResource>("widget.custom").Path);
        Assert.Equal("Content/widget.custom", library.Load<CustomResource>("Content/widget.custom").Path);
        Assert.Equal("Assets/widget.custom", library.Load<CustomResource>("Assets/widget.custom").Path);
    }

    [Fact]
    public void Load_ByType_ReturnsTheResource()
    {
        var library = CreateLibrary();

        var model = library.Load(typeof(Model), "Content/Models/Crate/Crate.gltf");

        Assert.IsType<Model>(model);
        Assert.Same(model, library.Load<Model>("Content/Models/Crate/Crate.gltf"));
    }

    [Fact]
    public void Load_UnknownResourceType_Throws()
    {
        var library = new ResourceLibrary();

        var error = Assert.Throws<InvalidOperationException>(() => library.Load<CustomResource>("Assets/Foo.custom"));

        Assert.Contains("No loader registered", error.Message);
    }

    [Fact]
    public void RegisterLoader_CustomResource_LoadsSharesAndClears()
    {
        var library = new ResourceLibrary();
        var loaded = 0;
        library.RegisterLoader<CustomResource>(path =>
        {
            loaded++;
            return new CustomResource(path);
        });

        var path = "Assets/Data/settings.custom";

        var first = library.Load<CustomResource>(path);
        var second = library.Load<CustomResource>(path);

        Assert.Same(first, second);
        Assert.Equal(1, loaded); // imported once, shared by path
        Assert.True(library.TryGet(path, out CustomResource? cached));
        Assert.Same(first, cached);
        Assert.Equal(path, first.Path);

        // GetAll lists the cached entry; Clear discards it.
        var all = library.GetAll<CustomResource>();
        Assert.Contains(first, all);
        library.Clear<CustomResource>();
        Assert.Empty(library.GetAll<CustomResource>());
        Assert.Equal(0, library.CachedCount<CustomResource>());
    }

    [Fact]
    public void Invalidate_DiscardsTheEntrySoTheNextLoadRunsTheLoaderAgain()
    {
        var library = new ResourceLibrary();
        library.RegisterLoader<CustomResource>(path => new CustomResource(path));

        var path = "Assets/Data/settings.custom";
        var first = library.Load<CustomResource>(path);

        library.Invalidate<CustomResource>(path);

        Assert.Equal(0, library.CachedCount<CustomResource>());
        Assert.False(library.TryGet(path, out CustomResource? _));

        var second = library.Load<CustomResource>(path);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Invalidate_ByPath_ResolvesTheTypeFromTheExtension()
    {
        var library = new ResourceLibrary();
        library.RegisterLoader<CustomResource>(path => new CustomResource(path));

        var path = "Assets/Data/settings.custom";
        library.Load<CustomResource>(path);
        Assert.Equal(1, library.CachedCount<CustomResource>());

        // The content hot-reload path: invalidate without naming the type —
        // the extension registry resolves it (.custom → CustomResource).
        Assert.True(library.Invalidate(path));
        Assert.Equal(0, library.CachedCount<CustomResource>());
        Assert.False(library.TryGet(path, out CustomResource? _));
    }

    [Fact]
    public void Invalidate_ByPath_UnknownExtension_IsANoOp()
    {
        var library = CreateLibrary();

        // No registered resource type loads .txt: not an asset, nothing to drop.
        Assert.False(library.Invalidate("Assets/Data/notes.txt"));
    }

    [Fact]
    public void DiscardedEntries_AreDisposed()
    {
        var library = new ResourceLibrary();
        var loaded = new List<TrackingResource>();
        library.RegisterLoader<TrackingResource>(path =>
        {
            var resource = new TrackingResource();
            loaded.Add(resource);
            return resource;
        });

        library.Load<TrackingResource>("Assets/Data/a.custom");
        library.Load<TrackingResource>("Assets/Data/b.custom");
        Assert.All(loaded, resource => Assert.Equal(0, resource.UnloadCount));

        library.Clear<TrackingResource>();

        Assert.All(loaded, resource => Assert.Equal(1, resource.UnloadCount));
    }

    [Fact]
    public void Register_AssemblyWithUnmarkedResourceFile_Throws()
    {
        var library = new ResourceLibrary();

        // The test assembly contains UnmarkedResource, a ResourceFile subclass
        // without the mandatory [AssetType] marker: registration must reject it.
        var error = Assert.Throws<InvalidOperationException>(
            () => library.Register(typeof(ResourceLibraryTests).Assembly));

        Assert.Contains("UnmarkedResource", error.Message);
        Assert.Contains("[AssetType]", error.Message);
    }

    [Fact]
    public void RegisterLoader_UnmarkedResourceType_Throws()
    {
        var library = new ResourceLibrary();

        var error = Assert.Throws<InvalidOperationException>(
            () => library.RegisterLoader<UnmarkedResource>(path => new UnmarkedResource()));

        Assert.Contains("UnmarkedResource", error.Message);
        Assert.Contains("[AssetType]", error.Message);
    }

    [Fact]
    public void RegisterAssembly_DiscoversResourceTypes()
    {
        // Registering an assembly discovers every [AssetType]-marked type in
        // it (Model, Texture2D, AudioClip, Shader) with no explicit loader
        // registration.
        var library = new ResourceLibrary();
        library.Register(typeof(Model).Assembly);

        var model = library.Load<Model>("Content/Models/Crate/Crate.gltf");

        Assert.NotNull(model);
        Assert.Equal(1, library.CachedCount<Model>());
    }

    [Fact]
    public void Register_IsIdempotent()
    {
        var library = new ResourceLibrary();
        library.Register(typeof(Model).Assembly);

        var first = library.Load<Model>("Content/Models/Crate/Crate.gltf");

        // Registering the same assembly again must not reset the caches.
        library.Register(typeof(Model).Assembly);

        Assert.Same(first, library.Load<Model>("Content/Models/Crate/Crate.gltf"));
        Assert.Equal(1, library.CachedCount<Model>());
    }

    [Fact]
    public void Unregister_RemovesTheAssemblysResourceTypes()
    {
        var library = new ResourceLibrary();
        library.Register(typeof(Model).Assembly);
        library.Load<Model>("Content/Models/Crate/Crate.gltf");
        Assert.Equal(1, library.CachedCount<Model>());

        library.Unregister(typeof(Model).Assembly);

        // The types no longer resolve: loading a model throws like a type
        // that was never registered.
        var error = Assert.Throws<InvalidOperationException>(() => library.Load<Model>("Content/Models/Crate/Crate.gltf"));
        Assert.Contains("No loader registered", error.Message);
    }

    [Fact]
    public void RetainAndRelease_DiscardTheEntryWhenTheLastHolderReleases()
    {
        var library = CreateLibrary();

        var path = "Content/Models/Crate/Crate.gltf";
        var model = library.Load<Model>(path);
        Assert.Equal(0, library.GetReferenceCount<Model>(path));

        library.Retain<Model>(path);
        Assert.Equal(1, library.GetReferenceCount<Model>(path));

        library.Release<Model>(path);
        Assert.Equal(0, library.GetReferenceCount<Model>(path));
        Assert.Equal(0, library.CachedCount<Model>()); // entry discarded
    }

    [Fact]
    public void LoadModel_MissingFile_ReturnsErrorModelWithoutCachingTheFailure()
    {
        var library = CreateLibrary();

        var model = library.LoadModel("Content/Models/Missing/Missing.gltf");

        Assert.Same(Model.Error, model);
        // The failure is not cached: no entry, so a later call re-attempts.
        Assert.Equal(0, library.CachedCount<Model>());
    }

    [Fact]
    public async Task LoadAsync_InstallsIntoTheSameCacheAsSyncLoad()
    {
        var path = WriteCubeObj();
        try
        {
            // Model.LoadAsync runs the Assimp import off-thread and installs
            // the result in the global cache, so the next sync load returns
            // the very same instance (no second import).
            var model = await Model.LoadAsync(path);

            Assert.Same(model, Model.Load(path));
        }
        finally
        {
            Model.Invalidate(path);
            File.Delete(path);
        }
    }

    private static string WriteCubeObj()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cube-{Guid.NewGuid():N}.obj");
        File.WriteAllText(path, """
                               o Cube
                               v -0.5 -0.5 -0.5
                               v  0.5 -0.5 -0.5
                               v  0.5  0.5 -0.5
                               v -0.5  0.5 -0.5
                               v -0.5 -0.5  0.5
                               v  0.5 -0.5  0.5
                               v  0.5  0.5  0.5
                               v -0.5  0.5  0.5
                               f 1 3 2
                               f 1 4 3
                               f 2 3 7
                               f 2 7 6
                               f 6 7 8
                               f 6 8 5
                               f 5 8 4
                               f 5 4 1
                               f 4 8 7
                               f 4 7 3
                               f 5 1 2
                               f 5 2 6
                               """);
        return path;
    }
}
