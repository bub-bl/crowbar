using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine.Tests;

public sealed class VolumetricFogTests
{
    [Fact]
    public void VolumetricFog_IsADiscoverablePostProcess()
    {
        Assert.True(typeof(VolumetricFog).IsSubclassOf(typeof(PostProcess)));
        Assert.True(typeof(VolumetricFogVolume).IsSubclassOf(typeof(TransformComponent)));

        using var world = new World();
        var level = world.CreateLevel("VolumetricFog");
        world.SpawnEntity("Fog", level).AddComponent<VolumetricFog>();
        Assert.Single(world.Query<PostProcess>().OfType<VolumetricFog>());
    }

    [Fact]
    public void VolumetricFog_DefaultsAreSane()
    {
        Assert.Equal(300f, new VolumetricFog().Order);
        Assert.Equal(0.02f, new VolumetricFog().Density);
        Assert.True(new VolumetricFog().SliceCount >= 2);
    }

    [Fact]
    public void VolumetricFogVolume_DefaultsToASizeableBox()
    {
        using var world = new World();
        var level = world.CreateLevel("VolumetricFog");
        var entity = world.SpawnEntity("FogVolume", level);
        var volume = entity.AddComponent<VolumetricFogVolume>();
        volume.Local = new Transform(new Vector3(10f, 20f, 30f), Rotation.Identity, Vector3.One);

        volume.GetWorldBounds(out var center, out var halfExtents);
        Assert.Equal(new Vector3(10f, 20f, 30f), center);
        Assert.Equal(volume.HalfExtents, halfExtents);
    }

    [Fact]
    public void VolumetricFogVolume_ScalesWithTheEntityTransform()
    {
        using var world = new World();
        var level = world.CreateLevel("VolumetricFog");
        var entity = world.SpawnEntity("FogVolume", level);
        var volume = entity.AddComponent<VolumetricFogVolume>();
        volume.HalfExtents = new Vector3(10f, 20f, 30f);
        volume.Local = new Transform(Vector3.Zero, Rotation.Identity, new Vector3(2f, 3f, 4f));

        volume.GetWorldBounds(out _, out var halfExtents);
        Assert.Equal(new Vector3(20f, 60f, 120f), halfExtents);
    }

    [Fact]
    public void FogAccumulateShader_ExposesFogUniforms()
    {
        var shader = Shader.Load("Shaders/PostProcesses/FogAccumulate.wgsl");
        Assert.Equal(ShaderStageKind.Compute, Assert.Single(shader.EntryPoints).Stage);
        var uniforms = Assert.Single(shader.Structs, structure => structure.Name == "FogAccumulateUniforms");
        Assert.Contains(uniforms.Fields, field => field.Name == "fogParams");
        Assert.Contains(uniforms.Fields, field => field.Name == "cameraBasis");
        // The volume's RGBA16F storage is inferred from the runtime normalization.
        Assert.Contains(shader.Bindings, binding =>
            binding.VariableName == "outScattering" && binding.Kind == ShaderBindingKind.StorageTexture);
        Assert.Contains(shader.Bindings, binding =>
            binding.VariableName == "shadows" && binding.Kind == ShaderBindingKind.UniformBuffer);
        Assert.Contains(shader.Bindings, binding =>
            binding.VariableName == "shadowMap" && binding.Kind == ShaderBindingKind.Texture);
        Assert.Contains(shader.Bindings, binding => binding.VariableName == "shadowSampler");
    }

    [Fact]
    public void FogIntegrateShader_ExposesSliceUniformsAndArrayRead()
    {
        var shader = Shader.Load("Shaders/PostProcesses/FogIntegrate.wgsl");
        Assert.Equal(ShaderStageKind.Compute, Assert.Single(shader.EntryPoints).Stage);
        var uniforms = Assert.Single(shader.Structs, structure => structure.Name == "FogIntegrateUniforms");
        Assert.Contains(uniforms.Fields, field => field.Name == "depths");
        Assert.Contains(shader.Bindings, binding =>
            binding.VariableName == "scatteringVolume" && binding.Kind == ShaderBindingKind.Texture);
    }

    [Fact]
    public void FogApplyShader_ExposesTheSceneAndFogBindings()
    {
        var shader = Shader.Load("Shaders/PostProcesses/FogApply.wgsl");
        Assert.Contains(shader.EntryPoints, entry => entry.Name == "fs_main" && entry.Stage == ShaderStageKind.Fragment);
        Assert.Contains(shader.Bindings, binding =>
            binding.VariableName == "fogVolume" && binding.TypeName == "texture_2d_array<f32>");
        Assert.Contains(shader.Bindings, binding =>
            binding.VariableName == "sceneDepth" && binding.TypeName == "texture_depth_2d");
        Assert.Contains(shader.Structs, structure => structure.Name == "FogApplyUniforms");
    }
}