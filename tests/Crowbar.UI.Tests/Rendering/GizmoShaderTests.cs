namespace Crowbar.Engine.Tests;

public class GizmoShaderTests
{
    [Fact]
    public void GizmoLineShader_BindsSceneAndWidget()
    {
        var shader = Shader.Load("Shaders/GizmoLine.wgsl");

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

        // The arrowheads are actual 3D cones, not billboard triangles.
        Assert.Contains("cross(axis, reference)", shader.Source);
        Assert.Contains("element.sizes.z", shader.Source);

        // A viewport overlay: no material struct, no textures.
        Assert.Empty(shader.MaterialFields);
    }

    [Fact]
    public void GizmoSpriteShader_BindsSceneAndSpriteStorage()
    {
        var shader = Shader.Load("Shaders/GizmoSprite.wgsl");

        var technique = Assert.Single(shader.Techniques);
        Assert.Equal("Main", technique.Name);

        var bindings = shader.Bindings;
        Assert.Equal(2, bindings.Count);
        Assert.Equal((0, 1u, ShaderBindingKind.ReadOnlyStorageBuffer, "sprites", "array<GizmoSpriteParams>"),
            (bindings[1].Group, bindings[1].Slot, bindings[1].Kind, bindings[1].VariableName, bindings[1].TypeName));

        Assert.Empty(shader.MaterialFields);
    }
}
