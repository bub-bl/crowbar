using Crowbar.Engine.Rendering;

namespace Crowbar.Engine.Tests;

public class ShaderReflectionTests
{
    [Fact]
    public void Load_ReturnsTheSameInstanceForTheSamePath()
    {
        // Shaders are cached by content path (like models and textures): the
        // disk read + reflection sidecar parse happens once per shader. This
        // is what keeps material restore cheap — every undo/redo rebuilds each
        // material through Material.FromShader, which must not re-read files.
        Assert.Same(
            Shader.Load("Shaders/Surface/Standard.wgsl"),
            Shader.Load("Shaders/Surface/Standard.wgsl"));
    }

    [Fact]
    public void StandardShader_ExposesTheMainTechnique()
    {
        var shader = Shader.Load("Shaders/Surface/Standard.wgsl");

        Assert.Equal(new[] { "Main" }, shader.Techniques.Select(t => t.Name).ToArray());
        var main = shader.GetTechnique("Main");
        Assert.Equal(("vs_main", "fs_main"), (main.VertexEntryPoint, main.FragmentEntryPoint));
    }

    [Fact]
    public void StandardShader_BindingsMatchTheEngineLayout()
    {
        var shader = Shader.Load("Shaders/Surface/Standard.wgsl");

        // Group 0 = per-frame camera + lights + shadows (from the includes);
        // group 1 = per-renderable model + material.
        var expected = new[]
        {
            (0, 0u, ShaderBindingKind.UniformBuffer, "camera", "CameraUniforms"),
            (0, 1u, ShaderBindingKind.UniformBuffer, "lights", "LightsUniform"),
            (0, 2u, ShaderBindingKind.UniformBuffer, "shadows", "ShadowUniforms"),
            (0, 3u, ShaderBindingKind.Texture, "shadowMap", "texture_depth_2d"),
            (0, 4u, ShaderBindingKind.Sampler, "shadowSampler", "sampler"),
            (1, 0u, ShaderBindingKind.UniformBuffer, "model", "mat4x4<f32>"),
            (1, 1u, ShaderBindingKind.UniformBuffer, "material", "MaterialUniforms")
        };
        Assert.Equal(
            expected,
            shader.Bindings.Select(b => (b.Group, b.Slot, b.Kind, b.VariableName, b.TypeName)).ToArray());
    }

    [Fact]
    public void StandardShader_MaterialFieldsComeFromTheMaterialStruct()
    {
        var shader = Shader.Load("Shaders/Surface/Standard.wgsl");

        Assert.Equal(
            new[] { "color", "metallic", "roughness", "emissive" },
            shader.MaterialFields.Select(f => f.Name).ToArray());
        Assert.Equal("vec4<f32>", shader.MaterialFields[0].Type);
        Assert.All(shader.MaterialFields.Skip(1), field => Assert.Equal("f32", field.Type));

        // The uniform layout comes straight from slangc's reflection sidecar:
        // color spans bytes 0..16, then metallic/roughness/emissive at 16/20/24.
        Assert.Equal(
            new[] { (0, 16, 16), (16, 4, 4), (20, 4, 4), (24, 4, 4) },
            shader.MaterialFields.Select(f => (f.Offset, f.Size, f.Alignment)).ToArray());

        // The material struct is what Material.Set validates against.
        Assert.Equal(shader.MaterialFields.Select(f => f.Name), shader.Parameters.Select(p => p.Name));
    }

    [Fact]
    public void StandardPbrShader_ExposesTextureSlotsAndASampler()
    {
        var shader = Shader.Load("Shaders/Surface/StandardPbr.wgsl");

        Assert.Equal(
            new[] { "albedoTexture", "normalTexture", "metallicRoughnessTexture", "occlusionTexture", "emissiveTexture" },
            shader.Bindings.Where(b => b.Kind == ShaderBindingKind.Texture && b.Group == 1).Select(b => b.VariableName).ToArray());
        Assert.Contains(shader.Bindings,
            b => b.VariableName == "materialSampler" && b.Kind == ShaderBindingKind.Sampler && b.Group == 1);
        Assert.Contains(shader.Bindings,
            b => b.VariableName == "shadowMap" && b.Kind == ShaderBindingKind.Texture && b.Group == 0);
        Assert.Contains(shader.MaterialFields, f => f.Name == "occlusion");
    }

    [Fact]
    public void UnlitShader_ExposesTheMainTechnique()
    {
        var shader = Shader.Load("Shaders/Surface/Unlit.wgsl");

        Assert.Single(shader.Techniques);
        Assert.Equal("Main", shader.Techniques[0].Name);
        Assert.Equal(
            new[] { "color", "metallic", "roughness", "emissive" },
            shader.MaterialFields.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void UnknownTechnique_Throws()
    {
        var shader = Shader.Load("Shaders/Surface/Standard.wgsl");
        Assert.Throws<InvalidOperationException>(() => shader.GetTechnique("DoesNotExist"));
    }

    [Fact]
    public void BuildBindGroupLayouts_DerivesTheMeshPipelineLayout()
    {
        // Group 0 = per-frame camera + lights + shadows (with the depth map);
        // group 1 = per-renderable model + material. The one binding slangc's
        // reflection cannot classify — the shadow comparison sampler — comes
        // back as a plain sampler, which is why the engine keeps that shared
        // group explicit in Renderer.FrameGroupBindings.
        var shader = Shader.Load("Shaders/Surface/Standard.wgsl");
        var layouts = shader.BuildBindGroupLayouts();

        Assert.Equal(2, layouts.Count);
        Assert.Equal(
            new[]
            {
                (0u, BindingType.UniformBuffer),
                (1u, BindingType.UniformBuffer),
                (2u, BindingType.UniformBuffer),
                (3u, BindingType.DepthTexture),
                (4u, BindingType.Sampler)
            },
            layouts[0].Select(b => (b.Slot, b.Type)).ToArray());
        Assert.Equal(
            new[] { (0u, BindingType.UniformBuffer), (1u, BindingType.UniformBuffer) },
            layouts[1].Select(b => (b.Slot, b.Type)).ToArray());
    }

    [Fact]
    public void UiShaderLayouts_MatchTheRendererContracts()
    {
        var shape = Shader.Load("Shaders/Ui/Shape.wgsl");
        var shapeLayout = Assert.Single(shape.BuildBindGroupLayouts());
        Assert.Equal(
            new[] { (0u, BindingType.ReadOnlyStorageBuffer), (1u, BindingType.UniformBuffer) },
            shapeLayout.Select(b => (b.Slot, b.Type)).ToArray());

        var glyph = Shader.Load("Shaders/Ui/Glyph.wgsl");
        var glyphLayout = Assert.Single(glyph.BuildBindGroupLayouts());
        Assert.Equal(
            new[] { (0u, BindingType.Texture), (1u, BindingType.Sampler), (2u, BindingType.UniformBuffer) },
            glyphLayout.Select(b => (b.Slot, b.Type)).ToArray());
        // Textures/samplers are exposed to the fragment stage only; buffers to
        // both stages (a conservative superset WebGPU accepts).
        Assert.Equal(ShaderStage.Fragment, glyphLayout[0].Stages);
        Assert.Equal(ShaderStage.Vertex | ShaderStage.Fragment, glyphLayout[2].Stages);
    }

    [Fact]
    public void ShadowDepthLayout_HasOneUniformPerGroup()
    {
        var shader = Shader.Load("Shaders/Surface/ShadowDepth.wgsl");
        var layouts = shader.BuildBindGroupLayouts();

        Assert.Equal(2, layouts.Count);
        Assert.All(layouts, group => Assert.Equal(
            new[] { (0u, BindingType.UniformBuffer) },
            group.Select(b => (b.Slot, b.Type)).ToArray()));
    }
}
