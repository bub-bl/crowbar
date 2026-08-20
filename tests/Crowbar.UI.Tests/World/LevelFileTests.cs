using System.Numerics;
using System.Text.Json;
using Crowbar.FileSystems;

namespace Crowbar.Engine.Tests;

public class LevelFileTests
{
    private static Level BuildRoundTripLevel(World world)
    {
        var level = world.CreateLevel("RoundTrip");

        var sun = level.SpawnEntity("Sun");
        var sunLight = sun.AddComponent<DirectionalLight>();
        sunLight.Color = new Vector3(1f, 0.9f, 0.8f);
        sunLight.Intensity = 1.5f;
        sunLight.CastShadows = true;
        sunLight.Local = new Transform(
            new Vector3(1f, 2f, 3f),
            Rotation.FromYaw(30f) * Rotation.FromPitch(15f),
            Vector3.One);

        var fill = level.SpawnEntity("Fill");
        var fillLight = fill.AddComponent<PointLight>();
        fillLight.Color = new Vector3(0.4f, 0.6f, 1f);
        fillLight.Intensity = 4f;
        fillLight.Range = 8f;
        fillLight.Local = new Transform(new Vector3(-2.5f, 2f, -1.5f), Rotation.Identity, Vector3.One);

        var cube = level.SpawnEntity("Cube");
        var mesh = cube.AddComponent<MeshRenderer>();
        mesh.Model = Model.CreateCube();
        mesh.Material = Material.FromShader("Surface/StandardPbr")
            .Set("color", new Vector4(0.2f, 0.6f, 1f, 1f))
            .Set("metallic", 0.15f)
            .Set("roughness", 0.45f);
        mesh.Material!.BlendMode = MaterialBlendMode.Blend;
        mesh.Material.DoubleSided = true;
        mesh.Local = new Transform(new Vector3(0f, 0.5f, 0f), Rotation.Identity, Vector3.One);

        var plane = level.SpawnEntity("Plane");
        var planeMesh = plane.AddComponent<MeshRenderer>();
        planeMesh.Model = Model.CreatePlane();
        planeMesh.Local = new Transform(Vector3.Zero, Rotation.Identity, new Vector3(5f, 1f, 5f));

        return level;
    }

    private static Level RoundTrip(Level source, out World world)
    {
        var json = LevelSerializer.Serialize(source);
        var data = LevelSerializer.Deserialize(json);
        world = new World();
        return LevelSerializer.CreateLevel(world, data);
    }

    [Fact]
    public void RoundTrip_PreservesEntitiesComponentsAndValues()
    {
        using var sourceWorld = new World();
        var source = BuildRoundTripLevel(sourceWorld);
        var json = LevelSerializer.Serialize(source);

        using var world = new World();
        var loaded = LevelSerializer.CreateLevel(world, LevelSerializer.Deserialize(json));

        Assert.Equal(source.Name, loaded.Name);
        Assert.Equal(source.Id, loaded.Id);
        Assert.Equal(source.Entities.Count, loaded.Entities.Count);

        // Same spawn order, names and stable ids.
        for (var i = 0; i < source.Entities.Count; i++)
        {
            Assert.Equal(source.Entities[i].Name, loaded.Entities[i].Name);
            Assert.Equal(source.Entities[i].Id, loaded.Entities[i].Id);
        }

        var sun = world.FindEntity(source.Entities[0].Id)!;
        var sunLight = sun.GetComponent<DirectionalLight>();
        Assert.NotNull(sunLight);
        Assert.Equal(1.5f, sunLight!.Intensity, 3);
        Assert.True(sunLight.CastShadows);
        Assert.Equal(0.9f, sunLight.Color.Y, 3);
        Assert.True(sunLight.Local.AlmostEqual(new Transform(
            new Vector3(1f, 2f, 3f),
            Rotation.FromYaw(30f) * Rotation.FromPitch(15f),
            Vector3.One)));

        var fillLight = world.FindEntity(source.Entities[1].Id)!.GetComponent<PointLight>();
        Assert.NotNull(fillLight);
        Assert.Equal(8f, fillLight!.Range, 3);
        Assert.Equal(4f, fillLight.Intensity, 3);

        var mesh = world.FindEntity(source.Entities[2].Id)!.GetComponent<MeshRenderer>();
        Assert.NotNull(mesh);
        Assert.NotNull(mesh!.Model);
        Assert.True(mesh.Model!.IsProcedural);
        Assert.Equal("Cube", mesh.Model.Name);
        Assert.NotNull(mesh.Material);
        Assert.Equal(MaterialBlendMode.Blend, mesh.Material!.BlendMode);
        Assert.True(mesh.Material.DoubleSided);
        Assert.True(mesh.Material.TryGet("color", out Vector4 color));
        Assert.Equal(0.6f, color.Y, 3);
        Assert.True(mesh.Material.TryGet("metallic", out float metallic));
        Assert.Equal(0.15f, metallic, 3);

        var planeMesh = world.FindEntity(source.Entities[3].Id)!.GetComponent<MeshRenderer>();
        Assert.Equal("Plane", planeMesh!.Model!.Name);
        Assert.True(planeMesh.Local.AlmostEqual(new Transform(Vector3.Zero, Rotation.Identity, new Vector3(5f, 1f, 5f))));
    }

    [Fact]
    public void RoundTrip_PreservesTransformAttachments()
    {
        using var sourceWorld = new World();
        var level = sourceWorld.CreateLevel("Hierarchy");

        var parent = level.SpawnEntity("Parent");
        var parentMesh = parent.AddComponent<MeshRenderer>();
        parentMesh.Model = Model.CreateCube();
        parentMesh.Local = new Transform(new Vector3(2f, 0f, 0f), Rotation.Identity, Vector3.One);

        var child = level.SpawnEntity("Child");
        var childMesh = child.AddComponent<MeshRenderer>();
        childMesh.Model = Model.CreateCube();
        childMesh.Local = new Transform(new Vector3(1f, 1f, 0f), Rotation.Identity, Vector3.One);
        var childWorld = childMesh.World.Position;
        child.AttachTo(parent, keepWorldTransform: true);

        var json = LevelSerializer.Serialize(level);
        using var world = new World();
        var loaded = LevelSerializer.CreateLevel(world, LevelSerializer.Deserialize(json));

        var loadedChild = world.FindEntity(child.Id)!;
        var loadedParent = world.FindEntity(parent.Id)!;
        var loadedChildMesh = loadedChild.GetComponent<MeshRenderer>()!;
        var loadedParentMesh = loadedParent.GetComponent<MeshRenderer>()!;

        Assert.NotNull(loadedChildMesh.Parent);
        Assert.Same(loadedParentMesh, loadedChildMesh.Parent);
        // Attaching with keepWorldTransform:false reproduces the saved locals
        // exactly, so the world transform comes back identical.
        Assert.Equal(childWorld.X, loadedChildMesh.World.Position.X, 4);
        Assert.Equal(childWorld.Y, loadedChildMesh.World.Position.Y, 4);
        Assert.Equal(childWorld.Z, loadedChildMesh.World.Position.Z, 4);
    }

    [Fact]
    public void Serialize_WritesVersionedPrettyJson()
    {
        using var world = new World();
        var level = BuildRoundTripLevel(world);

        var json = LevelSerializer.Serialize(level);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(LevelFile.CurrentFormat, root.GetProperty("format").GetInt32());
        Assert.Equal(level.Id.ToString(), root.GetProperty("id").GetString());
        Assert.Equal(level.Name, root.GetProperty("metadata").GetProperty("name").GetString());
        Assert.Equal(level.Entities.Count, root.GetProperty("entities").GetArrayLength());
        // Pretty-printed (diff-friendly in VCS).
        Assert.Contains('\n', json);

        // Pin the format contract of the component properties: vectors as
        // arrays, transforms as their canonical string, materials and models
        // as nested objects, all camelCase.
        var sun = root.GetProperty("entities")[0];
        Assert.Equal("Sun", sun.GetProperty("name").GetString());
        var light = sun.GetProperty("components")[0];
        Assert.Equal("DirectionalLight", light.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, light.GetProperty("transform").ValueKind);
        Assert.Equal(JsonValueKind.Array, light.GetProperty("properties").GetProperty("Color").ValueKind);
        Assert.Equal(3, light.GetProperty("properties").GetProperty("Color").GetArrayLength());
        Assert.Equal(JsonValueKind.Number, light.GetProperty("properties").GetProperty("Intensity").ValueKind);
        Assert.Equal(JsonValueKind.True, light.GetProperty("properties").GetProperty("CastShadows").ValueKind);

        var cube = root.GetProperty("entities")[2];
        var mesh = cube.GetProperty("components")[0];
        Assert.Equal("MeshRenderer", mesh.GetProperty("type").GetString());
        Assert.Equal("cube", mesh.GetProperty("properties").GetProperty("Model").GetProperty("procedural").GetString());
        var material = mesh.GetProperty("properties").GetProperty("Material");
        Assert.Equal("Surface/StandardPbr", material.GetProperty("shader").GetString());
        Assert.Equal("Blend", material.GetProperty("blendMode").GetString());
        Assert.True(material.GetProperty("doubleSided").GetBoolean());
        Assert.Equal(JsonValueKind.Array, material.GetProperty("values").GetProperty("color").ValueKind);
    }

    [Fact]
    public void ForwardCompatibility_UnknownComponentsAndPropertiesAreSkipped()
    {
        const string json = """
            {
              "format": 1,
              "id": "11111111-1111-1111-1111-111111111111",
              "metadata": { "name": "Future", "version": "9.9" },
              "entities": [
                {
                  "id": "22222222-2222-2222-2222-222222222222",
                  "name": "Hero",
                  "components": [
                    {
                      "type": "HologramProjector",
                      "transform": "1,2,3,0,0,0,1,1,1,1"
                    },
                    {
                      "type": "PointLight",
                      "transform": "0,1,0,0,0,0,1,1,1,1",
                      "properties": {
                        "Range": 5,
                        "FutureBeamColor": [1, 0, 0],
                        "Intensity": "not-a-number"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var warnings = new List<string>();
        using var world = new World();
        var data = LevelSerializer.Deserialize(json, warnings.Add);
        var level = LevelSerializer.CreateLevel(world, data, warnings.Add);

        // The unknown component is skipped; the known one survives with its
        // valid property restored and its invalid property ignored.
        var entity = Assert.Single(level.Entities);
        Assert.Equal("Hero", entity.Name);
        Assert.Equal(new Guid("22222222-2222-2222-2222-222222222222"), entity.Id);
        var light = Assert.IsType<PointLight>(Assert.Single(entity.Components));
        Assert.Equal(5f, light.Range, 3);
        Assert.Equal(1f, light.Intensity, 3); // default: the malformed value was skipped
        Assert.Contains(warnings, w => w.Contains("HologramProjector", StringComparison.Ordinal));
        // An unknown property is forward-compatible by design: skipped silently.
        // A known property whose value cannot be restored warns instead.
        Assert.DoesNotContain(warnings, w => w.Contains("FutureBeamColor", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("Intensity", StringComparison.Ordinal));
    }

    [Fact]
    public void Deserialize_NewerFormat_Throws()
    {
        const string json = """
            { "format": 999, "id": "11111111-1111-1111-1111-111111111111", "entities": [] }
            """;

        Assert.Throws<InvalidDataException>(() => LevelSerializer.Deserialize(json));
    }

    [Fact]
    public void SaveAndLoad_ThroughTheProjectFilesystem_PreservesTheLevel()
    {
        using var sourceWorld = new World();
        var source = BuildRoundTripLevel(sourceWorld);

        var path = "LevelFileTests_" + Guid.NewGuid().ToString("N") + ".level";
        try
        {
            LevelFile.Save(source, path);

            var file = LevelFile.Load(path);
            Assert.Equal(source.Id, file.Id);
            Assert.Equal(source.Name, file.Metadata?.Name);

            using var world = new World();
            var loaded = file.CreateLevel(world);
            Assert.Equal(source.Id, loaded.Id);
            Assert.Equal(source.Entities.Count, loaded.Entities.Count);

            // The save is atomic: no leftover temp file.
            Assert.False(FileSystem.Project.FileExists(path + ".tmp"));
        }
        finally
        {
            if (FileSystem.Project.FileExists(path)) FileSystem.Project.DeleteFile(path);
            if (FileSystem.Project.FileExists(path + ".tmp")) FileSystem.Project.DeleteFile(path + ".tmp");
        }
    }

    [Fact]
    public void ComponentTypeRegistry_ResolvesEngineComponentsAndRejectsUnknownNames()
    {
        Assert.Equal(typeof(MeshRenderer), ComponentTypeRegistry.Resolve("MeshRenderer"));
        Assert.Equal(typeof(PointLight), ComponentTypeRegistry.Resolve("PointLight"));
        Assert.Equal(typeof(Camera), ComponentTypeRegistry.Resolve("Camera"));
        Assert.Null(ComponentTypeRegistry.Resolve("HologramProjector"));
        Assert.Null(ComponentTypeRegistry.Resolve(""));
    }

    [Fact]
    public void RoundTrip_RegisteredGameComponent_IsLoadedWithItsProperties()
    {
        // A game-project component (like the editor's DemoComponent) only
        // resolves after its assembly is registered through the registry; the
        // level load must then pick it up and restore its properties. This is
        // the contract the editor relies on when it starts the game project
        // before loading the saved level.
        ComponentTypeRegistry.Register(typeof(DemoTestComponent));

        using var sourceWorld = new World();
        var source = sourceWorld.CreateLevel("GameComponents");
        var entity = source.SpawnEntity("Crate");
        entity.AddComponent<DemoTestComponent>().Name = "Démo";

        using var world = new World();
        var loaded = LevelSerializer.CreateLevel(world, LevelSerializer.Deserialize(LevelSerializer.Serialize(source)));

        var loadedEntity = Assert.Single(loaded.Entities);
        var component = Assert.IsType<DemoTestComponent>(Assert.Single(loadedEntity.Components));
        Assert.Equal("Démo", component.Name);
    }

    private sealed class DemoTestComponent : Component
    {
        [Property]
        public string? Name { get; set; } = "Démo";
    }
}
