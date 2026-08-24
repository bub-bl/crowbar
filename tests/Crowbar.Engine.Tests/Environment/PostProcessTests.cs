using Crowbar.Engine.Rendering;

namespace Crowbar.Engine.Tests;

public sealed class PostProcessTests
{
    [Fact]
    public void Tonemapping_PropertiesRoundTrip()
    {
        using var sourceWorld = new World();
        var source = sourceWorld.CreateLevel("PostProcess");
        var entity = sourceWorld.SpawnEntity("PostProcess", source);
        var component = entity.AddComponent<Tonemapping>();
        component.Operator = TonemapOperator.Agx;
        component.Exposure = 1.5f;
        component.Saturation = 0.8f;
        component.Order = 2;

        using var loadedWorld = new World();
        var loaded = LevelSerializer.CreateLevel(
            loadedWorld,
            LevelSerializer.Deserialize(LevelSerializer.Serialize(source)));

        var loadedComponent = Assert.Single(loaded.Entities).GetComponent<Tonemapping>();
        Assert.NotNull(loadedComponent);
        Assert.Equal(TonemapOperator.Agx, loadedComponent!.Operator);
        Assert.Equal(1.5f, loadedComponent.Exposure);
        Assert.Equal(0.8f, loadedComponent.Saturation);
        Assert.Equal(2, loadedComponent.Order);
        Assert.False(loaded.IsDirty);
    }

    [Fact]
    public void Tonemapping_DefaultsAreAcesWithNeutralExposureAndSaturation()
    {
        using var world = new World();
        var level = world.CreateLevel("PostProcess");
        var component = world.SpawnEntity("PostProcess", level).AddComponent<Tonemapping>();

        Assert.Equal(TonemapOperator.Aces, component.Operator);
        Assert.Equal(0f, component.Exposure);
        Assert.Equal(1f, component.Saturation);
        Assert.Equal(0, component.Order);
    }

    [Fact]
    public void PostProcess_IsAbstractAndTonemappingDerivesFromIt()
    {
        Assert.True(typeof(PostProcess).IsAbstract);
        Assert.True(typeof(PostProcess).IsSubclassOf(typeof(Component)));
        Assert.Equal(typeof(PostProcess), typeof(Tonemapping).BaseType);
        Assert.False(typeof(Tonemapping).IsAbstract);
    }

    [Fact]
    public void TonemappingShader_ExposesSettingsUniform()
    {
        var shader = Shader.Load("Shaders/PostProcesses/Tonemapping.wgsl");

        var uniforms = Assert.Single(shader.Structs, structure => structure.Name == "PostProcessUniforms");
        Assert.Contains(uniforms.Fields, field => field.Name == "settings");
        Assert.Contains(shader.Bindings, binding => binding.VariableName == "sceneTexture" && binding.Slot == 0u);
        Assert.Contains(shader.Bindings, binding => binding.VariableName == "sceneSampler" && binding.Slot == 1u);
        Assert.Contains(shader.Bindings, binding =>
            binding.VariableName == "postProcess" && binding.Slot == 2u &&
            binding.Kind == ShaderBindingKind.UniformBuffer);
    }
}
