using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Crowbar.Engine.Tests;

public class MaterialTests
{
    [Fact]
    public void FromShader_DefaultsToTheMainTechnique()
    {
        var material = Material.FromShader("Surface/Standard");

        Assert.Equal("Main", material.Technique);
        Assert.Equal("Standard", material.Shader.Name);
    }

    [Fact]
    public void FromShader_AcceptsANamedTechnique()
    {
        var material = Material.FromShader("Surface/Standard", "Main");

        Assert.Equal("Main", material.Technique);
    }

    [Fact]
    public void FromShader_RejectsAnUnknownTechnique()
    {
        Assert.Throws<InvalidOperationException>(() => Material.FromShader("Surface/Standard", "DoesNotExist"));
    }

    [Fact]
    public void Set_RejectsUnknownParameters()
    {
        var material = Material.FromShader("Surface/Standard");
        Assert.Throws<ArgumentException>(() => material.Set("lightDir", Vector3.UnitY));
    }

    [Fact]
    public void CreateDefault_SetsOnlyDeclaredParameters()
    {
        var material = Material.CreateDefault(Shader.Load("Shaders/Surface/Standard.wgsl"));

        Assert.True(material.TryGet<Vector4>("color", out var color));
        Assert.Equal(new Vector4(0.2f, 0.6f, 1f, 1f), color);
        Assert.True(material.TryGet<float>("metallic", out _));
        Assert.True(material.TryGet<float>("roughness", out _));
        Assert.True(material.TryGet<float>("emissive", out _));
    }

    [Fact]
    public void SetTexture_BindsToADeclaredSlot()
    {
        var material = Material.FromShader("Surface/StandardPbr");
        var texture = CreateTexture();

        material.SetTexture("albedoTexture", texture);

        Assert.Same(texture, material.Textures["albedoTexture"]);
    }

    [Fact]
    public void SetTexture_RejectsUnknownSlots()
    {
        var material = Material.FromShader("Surface/StandardPbr");
        Assert.Throws<ArgumentException>(() => material.SetTexture("diffuseMap", CreateTexture()));
    }

    private static Texture2D CreateTexture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"crowbar-mat-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = new Image<Rgba32>(1, 1))
            {
                image[0, 0] = new Rgba32(255, 255, 255, 255);
                image.Save(path);
            }

            return Texture2D.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
