namespace Crowbar.Engine.Tests;

public class ResourceCacheTests
{
    [Fact]
    public void Load_ReturnsTheSameModelInstanceForTheSamePath()
    {
        const string path = "Content/Models/Crate/Crate.gltf";

        var first = Model.Load(path);
        var second = Model.Load(path);

        Assert.Same(first, second);
        Assert.NotNull(first.ResourcePath);
    }

    [Fact]
    public void Load_SharesTextureInstancesAcrossLoads()
    {
        const string path = "Content/Models/Crate/Crate.gltf";

        var first = Model.Load(path);
        var second = Model.Load(path);

        // The same model instance is shared, so its materials bind the same
        // decoded Texture2D instances. The renderer uploads one GPU texture per
        // Texture2D (keyed by reference), so no duplicate GPU texture is made.
        Assert.Same(first, second);
        var firstMaterial = first.Meshes[0].Material!;
        var secondMaterial = second.Meshes[0].Material!;
        Assert.NotEmpty(firstMaterial.Textures);
        foreach (var slot in firstMaterial.Textures.Keys)
            Assert.Same(firstMaterial.Textures[slot], secondMaterial.Textures[slot]);
    }

    [Fact]
    public void RetainAndRelease_DiscardTheEntryWhenTheLastHolderReleases()
    {
        var path = WriteCubeObj();
        try
        {
            var model = Model.Load(path);
            Assert.Equal(0, Model.GetReferenceCount(path)); // the cache holds it; no holder yet

            model.Retain();
            Assert.Equal(1, Model.GetReferenceCount(path));

            model.Release();
            Assert.Equal(0, Model.GetReferenceCount(path)); // entry discarded

            var reloaded = Model.Load(path);
            Assert.NotSame(model, reloaded); // released, so it is imported again
        }
        finally
        {
            Model.Invalidate(path);
            File.Delete(path);
        }
    }

    [Fact]
    public void Invalidate_ForcesAReloadOfTheSamePath()
    {
        var path = WriteCubeObj();
        try
        {
            var first = Model.Load(path);
            Model.Invalidate(path);
            var second = Model.Load(path);

            Assert.NotSame(first, second);
        }
        finally
        {
            Model.Invalidate(path);
            File.Delete(path);
        }
    }

    [Fact]
    public void MeshRenderer_ReleasesItsModelReferenceWhenDestroyed()
    {
        var path = WriteCubeObj();
        var world = new World();
        try
        {
            var entity = world.SpawnEntity("Mesh");
            var renderer = entity.AddComponent<MeshRenderer>();
            renderer.Model = Model.Load(path);

            Assert.Equal(1, Model.GetReferenceCount(path));

            entity.Destroy();

            Assert.Equal(0, Model.GetReferenceCount(path));
        }
        finally
        {
            Model.Invalidate(path);
            File.Delete(path);
            world.Dispose();
        }
    }

    [Fact]
    public void ProceduralModels_HaveNoResourcePath()
    {
        Assert.Null(Model.CreateCube().ResourcePath);
        Assert.Null(Model.CreatePlane().ResourcePath);
        Assert.Null(Model.Error.ResourcePath);
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
