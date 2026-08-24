using Crowbar.Engine.Rendering;

namespace Crowbar.Engine.Tests;

public sealed class PostProcessTests
{
    [Fact]
    public void PostProcessComponent_PropertiesRoundTrip()
    {
        using var sourceWorld = new World();
        var source = sourceWorld.CreateLevel("PostProcess");
        var entity = sourceWorld.SpawnEntity("PostProcess", source);
        var component = entity.AddComponent<PostProcessComponent>();
        component.Operator = TonemapOperator.Agx;
        component.Exposure = 1.5f;
        component.Saturation = 0.8f;

        using var loadedWorld = new World();
        var loaded = LevelSerializer.CreateLevel(
            loadedWorld,
            LevelSerializer.Deserialize(LevelSerializer.Serialize(source)));

        var loadedComponent = Assert.Single(loaded.Entities).GetComponent<PostProcessComponent>();
        Assert.NotNull(loadedComponent);
        Assert.Equal(TonemapOperator.Agx, loadedComponent!.Operator);
        Assert.Equal(1.5f, loadedComponent.Exposure);
        Assert.Equal(0.8f, loadedComponent.Saturation);
        Assert.False(loaded.IsDirty);
    }

    [Fact]
    public void PostProcessComponent_DefaultsAreAcesWithNeutralExposureAndSaturation()
    {
        using var world = new World();
        var level = world.CreateLevel("PostProcess");
        var component = world.SpawnEntity("PostProcess", level).AddComponent<PostProcessComponent>();

        Assert.Equal(TonemapOperator.Aces, component.Operator);
        Assert.Equal(0f, component.Exposure);
        Assert.Equal(1f, component.Saturation);
    }

    [Fact]
    public void PostProcessShader_ExposesTonemapSettingsUniform()
    {
        var shader = Shader.Load("Shaders/Environment/PostProcess.wgsl");

        var uniforms = Assert.Single(shader.Structs, structure => structure.Name == "PostProcessUniforms");
        Assert.Contains(uniforms.Fields, field => field.Name == "settings");
        Assert.Contains(shader.Bindings, binding => binding.VariableName == "sceneTexture" && binding.Slot == 0u);
        Assert.Contains(shader.Bindings, binding => binding.VariableName == "sceneSampler" && binding.Slot == 1u);
        Assert.Contains(shader.Bindings, binding =>
            binding.VariableName == "postProcess" && binding.Slot == 2u &&
            binding.Kind == ShaderBindingKind.UniformBuffer);
    }
}
