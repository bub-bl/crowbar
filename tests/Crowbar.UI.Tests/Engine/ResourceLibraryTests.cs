using Crowbar.Engine.Global;

namespace Crowbar.Engine.Tests;

/// <summary>
/// Tests for the generic cache API of <see cref="ResourceLibrary"/>: per-type
/// caches owned by one library instance, sharing by path, loader registration
/// for custom resource types, and the invalidation/clear lifecycle.
/// </summary>
public class ResourceLibraryTests
{
    /// <summary>A custom resource type with no engine knowledge, inheriting <see cref="ResourceFile"/>.</summary>
    private sealed class CustomResource : ResourceFile
    {
        public CustomResource(string path)
        {
            Path = path;
            IsValid = true;
        }
    }

    [Fact]
    public void Load_SharesTheSameInstanceForTheSamePath()
    {
        var library = new ResourceLibrary();

        var first = library.Load<Model>("Assets/Models/Crate/Crate.gltf");
        var second = library.Load<Model>("Assets/Models/Crate/Crate.gltf");

        Assert.Same(first, second);
        Assert.Equal(1, library.CachedCount<Model>());
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
    public void ReleasedCallback_RunsWhenTheEntryIsDiscarded()
    {
        var library = new ResourceLibrary();
        var released = 0;
        library.RegisterLoader<CustomResource>(
            path => new CustomResource(path),
            _ => released++);

        library.Load<CustomResource>("Assets/Data/a.custom");
        library.Load<CustomResource>("Assets/Data/b.custom");
        Assert.Equal(0, released);

        library.Clear<CustomResource>();

        Assert.Equal(2, released);
    }

    [Fact]
    public void RetainAndRelease_DiscardTheEntryWhenTheLastHolderReleases()
    {
        var library = new ResourceLibrary();

        var path = "Assets/Models/Crate/Crate.gltf";
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
        var library = new ResourceLibrary();

        var model = library.LoadModel("Assets/Models/Missing/Missing.gltf");

        Assert.Same(Model.Error, model);
        // The failure is not cached: no entry, so a later call re-attempts.
        Assert.Equal(0, library.CachedCount<Model>());
    }

    [Fact]
    public async Task LoadAsync_InstallsIntoTheSameCacheAsSyncLoad()
    {
        var library = new ResourceLibrary();
        var path = WriteCubeObj();
        try
        {
            var model = await library.LoadAsync(path, _ => Model.Import(path));

            Assert.Same(model, library.Load<Model>(path));
            Assert.Equal(1, library.CachedCount<Model>());
        }
        finally
        {
            library.Invalidate<Model>(path);
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
