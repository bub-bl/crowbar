using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine.Tests;

public class GridTests
{
    [Fact]
    public void GridShader_HasASingleUniformBindingAndMainTechnique()
    {
        var shader = Shader.Load("Shaders/Grid.wgsl");

        var technique = Assert.Single(shader.Techniques);
        Assert.Equal("Main", technique.Name);

        var binding = Assert.Single(shader.Bindings);
        Assert.Equal(0, binding.Group);
        Assert.Equal(0u, binding.Slot);
        Assert.Equal(ShaderBindingKind.UniformBuffer, binding.Kind);
        Assert.Equal("GridUniforms", binding.TypeName);
    }

    [Fact]
    public void GridShader_UsesGridUniformsNotTheMaterialStruct()
    {
        var shader = Shader.Load("Shaders/Grid.wgsl");

        // The grid is an engine-owned pass: no material struct, no textures.
        Assert.Empty(shader.MaterialFields);
        Assert.Empty(shader.Bindings.Where(b => b.Group >= 1));
    }

    [Fact]
    public void CreateUniforms_InvertsViewAndProjection()
    {
        var grid = new Grid();
        var view = Matrix4x4.CreateLookAt(new Vector3(4, 3, 4), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 100f);

        var uniforms = grid.CreateUniforms(view, projection);

        // ViewInv * View and ProjInv * Proj must be identities (within float
        // precision), otherwise the shader's unprojection would be wrong.
        AssertIdentity(uniforms.ViewInv * uniforms.View);
        AssertIdentity(uniforms.ProjInv * uniforms.Proj);
        Assert.Equal(view, uniforms.View);
        Assert.Equal(projection, uniforms.Proj);
    }

    [Fact]
    public void CreateUniforms_EncodesTheSettings()
    {
        var grid = new Grid
        {
            Size = 20f,
            CellSize = 2f,
            FadeDistance = 30f,
            ShowAxes = false
        };

        var uniforms = grid.CreateUniforms(Matrix4x4.Identity, Matrix4x4.Identity);

        Assert.Equal(20f, uniforms.Settings.X);
        Assert.Equal(2f, uniforms.Settings.Y);
        Assert.Equal(30f, uniforms.Settings.Z);
        Assert.Equal(0f, uniforms.Settings.W); // axes hidden
    }

    [Fact]
    public void CreateUniforms_InfiniteGridEncodesZeroSize()
    {
        var grid = new Grid(); // Size is null by default

        var uniforms = grid.CreateUniforms(Matrix4x4.Identity, Matrix4x4.Identity);

        Assert.Equal(0f, uniforms.Settings.X);
        Assert.Equal(1f, uniforms.Settings.Y); // default cell size
    }

    [Fact]
    public void AxisColors_MatchTheGizmoConvention()
    {
        var grid = new Grid();

        // X = red, Z = blue, comme les gizmos et l'inspecteur (X/Y/Z → RGB).
        Assert.True(grid.XAxisColor.X > grid.XAxisColor.Z);
        Assert.True(grid.ZAxisColor.Z > grid.ZAxisColor.X);
    }

    [Fact]
    public void GridShader_MapsXToTheLineAlongXAndZToTheLineAlongZ()
    {
        var shader = Shader.Load("Shaders/Grid.wgsl");

        // L'axe X est la ligne le long de X (z ≈ 0), l'axe Z la ligne le long
        // de Z (x ≈ 0). Les inverser désalignait les couleurs de la grille et
        // celles des gizmos position/scale/rotation.
        Assert.Contains("onXAxis = abs(fragPos3D.z)", shader.Source);
        Assert.Contains("onZAxis = abs(fragPos3D.x)", shader.Source);
    }

    private static void AssertIdentity(Matrix4x4 matrix)
    {
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                var expected = row == col ? 1f : 0f;
                Assert.Equal(expected, Get(matrix, row, col), 4);
            }
        }
    }

    private static float Get(Matrix4x4 matrix, int row, int col) => (row * 4 + col) switch
    {
        0 => matrix.M11,
        1 => matrix.M12,
        2 => matrix.M13,
        3 => matrix.M14,
        4 => matrix.M21,
        5 => matrix.M22,
        6 => matrix.M23,
        7 => matrix.M24,
        8 => matrix.M31,
        9 => matrix.M32,
        10 => matrix.M33,
        11 => matrix.M34,
        12 => matrix.M41,
        13 => matrix.M42,
        14 => matrix.M43,
        _ => matrix.M44
    };
}
