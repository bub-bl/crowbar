using System.Numerics;

namespace Crowbar.Engine.Tests;

public class ModelTests
{
    [Fact]
    public void CreateCube_ProducesIndexedGeometryWithUnitNormals()
    {
        var model = Model.CreateCube();

        Assert.Equal("Cube", model.Name);
        Assert.Equal(1, model.InstanceCount);
        Assert.Single(model.Nodes);
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
    public void Winding_MatchesTheLeftHandedEngineConvention()
    {
        // The engine renders left-handed (Unity/DirectX): a front face winds
        // so its geometric normal (right-hand cross product) points opposite
        // its stored normal. Both procedural and Assimp-imported geometry must
        // follow that convention, or front faces get culled as back faces.
        foreach (var model in new[] { Model.CreateCube(), Model.Load("Assets/Models/Crate/Crate.gltf") })
        {
            foreach (var mesh in model.Meshes)
            {
                for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
                {
                    var v0 = mesh.Vertices[mesh.Indices[i]].Position;
                    var v1 = mesh.Vertices[mesh.Indices[i + 1]].Position;
                    var v2 = mesh.Vertices[mesh.Indices[i + 2]].Position;
                    var geometric = Vector3.Cross(v1 - v0, v2 - v0);
                    Assert.True(
                        Vector3.Dot(geometric, mesh.Vertices[mesh.Indices[i]].Normal) < 0f,
                        $"{model.Name}/{mesh.Name} triangle {i / 3} is wound for the wrong handedness");
                }
            }
        }
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

        // No alphaMode/doubleSided in the crate: single-sided and opaque.
        Assert.Equal(MaterialBlendMode.Opaque, material.BlendMode);
        Assert.False(material.DoubleSided);
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

        // Render state: the bulb is alpha-blended and both materials are
        // double-sided, so the renderer disables culling and renders the bulb
        // without depth writes, back-to-front.
        Assert.Equal(MaterialBlendMode.Blend, transparent.BlendMode);
        Assert.Equal(MaterialBlendMode.Opaque, opaque.BlendMode);
        Assert.True(transparent.DoubleSided);
        Assert.True(opaque.DoubleSided);

        // The node hierarchy is preserved (not baked into the vertices): the
        // Sketchfab wrapper nodes keep their names and two mesh instances draw
        // the two meshes.
        Assert.Equal(2, model.InstanceCount);
        Assert.Equal(2, model.MeshInstances.Count);
        Assert.NotNull(model.Root);
        Assert.Contains(model.Nodes, node => node.Name == "Sketchfab_model");
        Assert.Contains(model.Nodes, node => node.Name == "RootNode");
        Assert.Contains(model.Nodes, node => node.Name == "Industrial Light");
        Assert.NotEqual(model.Bounds.Min, model.Bounds.Max);
    }

    [Fact]
    public void Load_PreservesNodesAndInstancesTheSameMeshAtSeveralTransforms()
    {
        // A glTF with two nodes referencing one mesh: one unique mesh drawn at
        // two transforms (the basis of instancing).
        var buffer = new byte[48];
        WriteFloat(buffer, 0, 0f);
        WriteFloat(buffer, 4, 0f);
        WriteFloat(buffer, 8, 0f);
        WriteFloat(buffer, 12, 1f);
        WriteFloat(buffer, 16, 0f);
        WriteFloat(buffer, 20, 0f);
        WriteFloat(buffer, 24, 0f);
        WriteFloat(buffer, 28, 1f);
        WriteFloat(buffer, 32, 0f);
        BitConverter.GetBytes(0u).CopyTo(buffer, 36);
        BitConverter.GetBytes(1u).CopyTo(buffer, 40);
        BitConverter.GetBytes(2u).CopyTo(buffer, 44);

        var uri = "data:application/octet-stream;base64," + Convert.ToBase64String(buffer);
        var gltf =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"buffers\":[{\"byteLength\":48,\"uri\":\"" + uri + "\"}]," +
            "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":36,\"byteLength\":12}]," +
            "\"accessors\":[" +
            "{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}," +
            "{\"bufferView\":1,\"componentType\":5125,\"count\":3,\"type\":\"SCALAR\"}]," +
            "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}]," +
            "\"nodes\":[" +
            "{\"mesh\":0,\"name\":\"InstanceA\",\"translation\":[0,0,0]}," +
            "{\"mesh\":0,\"name\":\"InstanceB\",\"translation\":[10,0,0]}]," +
            "\"scenes\":[{\"nodes\":[0,1]}],\"scene\":0}";

        var path = Path.Combine(Path.GetTempPath(), $"instances-{Guid.NewGuid():N}.gltf");
        try
        {
            File.WriteAllText(path, gltf);

            var model = Model.Load(path);

            Assert.Equal(1, model.MeshCount);
            Assert.Equal(2, model.InstanceCount);
            Assert.Equal(2, model.MeshInstances.Count);
            Assert.Same(model.MeshInstances[0].Mesh, model.MeshInstances[1].Mesh);

            var a = model.MeshInstances[0].Node.WorldTransform.Translation;
            var b = model.MeshInstances[1].Node.WorldTransform.Translation;
            Assert.Equal(10f, Vector3.Distance(a, b), 2);

            // The per-instance bounds span both transforms.
            var extent = model.Bounds.Max - model.Bounds.Min;
            Assert.True(MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) >= 10f);
        }
        finally
        {
            Model.Invalidate(path);
            File.Delete(path);
        }
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

    private static void WriteFloat(byte[] buffer, int offset, float value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);

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
