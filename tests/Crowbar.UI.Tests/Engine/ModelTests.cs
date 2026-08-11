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
