using System.Numerics;

namespace Crowbar.Engine.Tests;

public class ModelTests
{
    [Fact]
    public void CreateCube_ProducesIndexedGeometryWithUnitNormals()
    {
        var model = Model.CreateCube();

        Assert.Equal("Cube", model.Name);
        var mesh = Assert.Single(model.Meshes);

        // 6 faces × 4 corners, 6 faces × 2 triangles.
        Assert.Equal(24, mesh.Vertices.Length);
        Assert.Equal(36, mesh.Indices.Length);

        foreach (var vertex in mesh.Vertices)
        {
            // Every corner of a closed 1×1×1 cube sits at ±0.5 on each axis
            // (a regression would collapse the faces onto the origin planes).
            Assert.Contains(vertex.Position.X, new[] { -0.5f, 0.5f });
            Assert.Contains(vertex.Position.Y, new[] { -0.5f, 0.5f });
            Assert.Contains(vertex.Position.Z, new[] { -0.5f, 0.5f });
            Assert.True(vertex.Normal.LengthSquared() > 0.99f);
        }

        // All 8 corners are present, and no face is coplanar with another:
        // each of the six normals appears exactly four times.
        Assert.Equal(8, mesh.Vertices.Select(v => v.Position).Distinct().Count());
        Assert.Equal(6, mesh.Vertices.GroupBy(v => v.Normal).Count());
        Assert.All(mesh.Vertices.GroupBy(v => v.Normal), group => Assert.Equal(4, group.Count()));

        foreach (var index in mesh.Indices)
            Assert.InRange(index, 0u, (uint)mesh.Vertices.Length - 1);
    }

    [Fact]
    public void CreatePlane_ProducesAFlatUpFacingQuad()
    {
        var model = Model.CreatePlane();

        Assert.Equal("Plane", model.Name);
        var mesh = Assert.Single(model.Meshes);

        // 4 corners, 2 triangles.
        Assert.Equal(4, mesh.Vertices.Length);
        Assert.Equal(6, mesh.Indices.Length);

        // A unit quad lying in the XZ plane: x/z at ±0.5, y exactly 0.
        foreach (var vertex in mesh.Vertices)
        {
            Assert.Equal(0f, vertex.Position.Y);
            Assert.Contains(vertex.Position.X, new[] { -0.5f, 0.5f });
            Assert.Contains(vertex.Position.Z, new[] { -0.5f, 0.5f });
            Assert.Equal(Vector3.UnitY, vertex.Normal);
        }

        // Both corners of the quad are present.
        Assert.Equal(4, mesh.Vertices.Select(v => v.Position).Distinct().Count());

        foreach (var index in mesh.Indices)
            Assert.InRange(index, 0u, (uint)mesh.Vertices.Length - 1);
    }

    [Fact]
    public void Load_ImportsAnObjFile()
    {
        var path = WriteCubeObj();

        try
        {
            var model = Model.Load(path);

            Assert.StartsWith("cube-", model.Name); // the model takes its name from the file
            var mesh = Assert.Single(model.Meshes);
            Assert.Equal(36, mesh.Indices.Length); // 12 triangles
            Assert.True(mesh.Vertices.Length >= 8);
            foreach (var vertex in mesh.Vertices)
                Assert.True(vertex.Normal.LengthSquared() > 0.99f); // GenerateSmoothNormals
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ImportsGltfMaterialsAndTextures()
    {
        // The committed sample crate ships next to the engine shaders in the
        // test output, so it exercises the real content-path resolution.
        var model = Model.Load("Assets/Models/Crate/Crate.gltf");

        Assert.Equal("Crate", model.Name);
        Assert.True(Model.HasRenderMeshes(model));
        Assert.Equal(1, model.MeshCount);
        Assert.Equal(1, model.MaterialCount);
        Assert.False(model.IsProcedural);
        Assert.False(model.IsError);

        var mesh = Assert.Single(model.Meshes);
        Assert.NotNull(mesh.Material);

        var material = mesh.Material!;
        Assert.Equal("StandardPbr", material.Shader.Name);
        Assert.Equal("Crate", material.Name);

        // glTF factors become PBR parameters and the texture set is bound to
        // the shader's slots.
        Assert.True(material.TryGet<Vector4>("color", out var baseColor));
        Assert.Equal(new Vector4(1f, 1f, 1f, 1f), baseColor);
        Assert.True(material.TryGet<float>("metallic", out var metallic));
        Assert.Equal(0.12f, metallic, 3);
        Assert.True(material.TryGet<float>("roughness", out var roughness));
        Assert.Equal(0.5f, roughness, 3);
        Assert.Contains("albedoTexture", material.Textures.Keys);
        Assert.Contains("metallicRoughnessTexture", material.Textures.Keys);
    }

    [Fact]
    public void Load_ImportsTheSketchfabWorkLight()
    {
        // The real Sketchfab sample (scene.bin + 4 textures committed next to
        // the .gltf) exercises the whole glTF pipeline on production geometry:
        // two PBR materials, two meshes, node transforms baked in.
        var model = Model.Load("Assets/Models/industrial_work_light/industrial_work_light.gltf");

        Assert.Equal("industrial_work_light", model.Name);
        Assert.True(Model.HasRenderMeshes(model));
        Assert.False(model.IsProcedural);
        Assert.False(model.IsError);
        Assert.Equal(2, model.MeshCount);
        Assert.Equal(2, model.MaterialCount);

        var transparent = model.Meshes[0].Material;
        var opaque = model.Meshes[1].Material;
        Assert.NotNull(transparent);
        Assert.NotNull(opaque);

        Assert.Equal("Industrial_Light_Transparent", transparent!.Name);
        Assert.Equal("Industrial_Light", opaque!.Name);
        Assert.All(new[] { transparent, opaque }, material =>
        {
            Assert.Equal("StandardPbr", material.Shader.Name);
            Assert.Contains("albedoTexture", material.Textures.Keys);
            Assert.Contains("metallicRoughnessTexture", material.Textures.Keys);
            Assert.Contains("normalTexture", material.Textures.Keys);
        });

        // The bulb's baseColorFactor carries alpha 0.75 through to the shader.
        Assert.True(transparent.TryGet<Vector4>("color", out var transparentColor));
        Assert.Equal(0.75f, transparentColor.W, 3);
    }

    [Fact]
    public void Load_GltfMissingExternalBuffer_ThrowsActionableError()
    {
        // A .gltf whose .bin is absent must fail loudly and name the missing
        // file rather than surfacing Assimp's generic import error.
        var directory = Path.Combine(Path.GetTempPath(), $"gltf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "broken.gltf"), """
                {"buffers":[{"byteLength":16,"uri":"scene.bin"}],"asset":{"version":"2.0"}}
                """);

            var error = Assert.Throws<FileNotFoundException>(
                () => Model.Load(Path.Combine(directory, "broken.gltf")));

            Assert.Contains("scene.bin", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => Model.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.obj")));
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
