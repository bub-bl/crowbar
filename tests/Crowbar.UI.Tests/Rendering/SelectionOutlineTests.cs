using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine.Tests;

public class SelectionOutlineTests
{
    [Fact]
    public void SelectionMaskShader_BindsSceneAndModelUniforms()
    {
        var shader = Shader.Load("Shaders/SelectionMask.wgsl");

        var technique = Assert.Single(shader.Techniques);
        Assert.Equal("Main", technique.Name);

        var bindings = shader.Bindings;
        Assert.Equal(2, bindings.Count);
        Assert.Equal((0, 0u, ShaderBindingKind.UniformBuffer, "scene", "SceneUniforms"),
            (bindings[0].Group, bindings[0].Slot, bindings[0].Kind, bindings[0].VariableName, bindings[0].TypeName));
        Assert.Equal((1, 0u, ShaderBindingKind.UniformBuffer, "model", "mat4x4<f32>"),
            (bindings[1].Group, bindings[1].Slot, bindings[1].Kind, bindings[1].VariableName, bindings[1].TypeName));

        // A silhouette mask: no material struct, no textures.
        Assert.Empty(shader.MaterialFields);
    }

    [Fact]
    public void SelectionOutlineShader_BindsSceneMaskSamplerAndParams()
    {
        var shader = Shader.Load("Shaders/SelectionOutline.wgsl");

        var technique = Assert.Single(shader.Techniques);
        Assert.Equal("Main", technique.Name);

        var bindings = shader.Bindings;
        Assert.Equal(4, bindings.Count);
        Assert.Equal((0, 0u, ShaderBindingKind.Texture, "scene_tex", "texture_2d<f32>"),
            (bindings[0].Group, bindings[0].Slot, bindings[0].Kind, bindings[0].VariableName, bindings[0].TypeName));
        Assert.Equal((0, 1u, ShaderBindingKind.Texture, "mask_tex", "texture_2d<f32>"),
            (bindings[1].Group, bindings[1].Slot, bindings[1].Kind, bindings[1].VariableName, bindings[1].TypeName));
        Assert.Equal((0, 2u, ShaderBindingKind.Sampler, "samp", "sampler"),
            (bindings[2].Group, bindings[2].Slot, bindings[2].Kind, bindings[2].VariableName, bindings[2].TypeName));
        Assert.Equal((0, 3u, ShaderBindingKind.UniformBuffer, "params", "OutlineParams"),
            (bindings[3].Group, bindings[3].Slot, bindings[3].Kind, bindings[3].VariableName, bindings[3].TypeName));

        Assert.Empty(shader.MaterialFields);
    }

    [Fact]
    public void SelectionOutline_DefaultsToAnOpaqueOrangeBand()
    {
        var outline = new SelectionOutline();

        Assert.True(outline.Enabled);
        Assert.Equal(new Vector3(1f, 0.62f, 0.12f), outline.Color);
        Assert.Equal(1f, outline.Opacity);
        Assert.Equal(4f, outline.Thickness);
    }
}
