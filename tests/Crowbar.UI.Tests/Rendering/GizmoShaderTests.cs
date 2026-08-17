namespace Crowbar.Engine.Tests;

public class GizmoShaderTests
{
    [Fact]
    public void GizmoLineShader_BindsSceneAndWidget()
    {
        var shader = Shader.Load("Shaders/Editor/GizmoLine.wgsl");

        var technique = Assert.Single(shader.Techniques);
        Assert.Equal("Main", technique.Name);

        var bindings = shader.Bindings;
        Assert.Equal(2, bindings.Count);
        Assert.Equal((0, 0u, ShaderBindingKind.UniformBuffer, "scene", "SceneUniforms"),
            (bindings[0].Group, bindings[0].Slot, bindings[0].Kind, bindings[0].VariableName, bindings[0].TypeName));
        // The widget is instanced from a storage array (thick shafts + arrowheads);
        // colors are computed on the CPU, so there is no widget uniform anymore.
        Assert.Equal((0, 1u, ShaderBindingKind.ReadOnlyStorageBuffer, "elements", "array<GizmoWidgetElement>"),
            (bindings[1].Group, bindings[1].Slot, bindings[1].Kind, bindings[1].VariableName, bindings[1].TypeName));

        // A viewport overlay: no material struct, no textures.
        Assert.Empty(shader.MaterialFields);
    }

    [Fact]
    public void GizmoSpriteShader_BindsSceneAndSpriteStorage()
    {
        var shader = Shader.Load("Shaders/Editor/GizmoSprite.wgsl");

        var technique = Assert.Single(shader.Techniques);
        Assert.Equal("Main", technique.Name);

        var bindings = shader.Bindings;
        Assert.Equal(4, bindings.Count);
        Assert.Equal((0, 1u, ShaderBindingKind.ReadOnlyStorageBuffer, "sprites", "array<GizmoSpriteParams>"),
            (bindings[1].Group, bindings[1].Slot, bindings[1].Kind, bindings[1].VariableName, bindings[1].TypeName));
        Assert.Equal((0, 2u, ShaderBindingKind.Texture, "iconAtlas", "texture_2d<f32>"),
            (bindings[2].Group, bindings[2].Slot, bindings[2].Kind, bindings[2].VariableName, bindings[2].TypeName));
        Assert.Equal((0, 3u, ShaderBindingKind.Sampler, "iconSampler", "sampler"),
            (bindings[3].Group, bindings[3].Slot, bindings[3].Kind, bindings[3].VariableName, bindings[3].TypeName));

        Assert.Empty(shader.MaterialFields);
    }
}
