namespace Crowbar.Engine.Tests;

public class ShaderReflectionTests
{
    [Fact]
    public void MeshShader_ExposesTheMainTechnique()
    {
        var shader = Shader.Load("Shaders/Mesh.wgsl");

        // The old inverted-hull "Outline" technique was removed: selection
        // contours are a real post-process (SelectionMask/SelectionOutline).
        Assert.Equal(new[] { "Main" }, shader.Techniques.Select(t => t.Name).ToArray());
        var main = shader.GetTechnique("Main");
        Assert.Equal(("vs_main", "fs_main"), (main.VertexEntryPoint, main.FragmentEntryPoint));
    }

    [Fact]
    public void MeshShader_BindingsMatchTheEngineLayout()
    {
        var shader = Shader.Load("Shaders/Mesh.wgsl");

        // Group 0 = per-frame scene + lights + shadows (from the includes);
        // group 1 = per-renderable model + material.
        var expected = new[]
        {
            (0, 0u, ShaderBindingKind.UniformBuffer, "scene", "SceneUniforms"),
            (0, 1u, ShaderBindingKind.UniformBuffer, "lights", "LightsUniform"),
            (0, 2u, ShaderBindingKind.UniformBuffer, "shadows", "ShadowUniforms"),
            (0, 3u, ShaderBindingKind.Texture, "shadowMap", "texture_depth_2d"),
            (1, 0u, ShaderBindingKind.UniformBuffer, "model", "mat4x4<f32>"),
            (1, 1u, ShaderBindingKind.UniformBuffer, "material", "MaterialUniforms")
        };
        Assert.Equal(
            expected,
            shader.Bindings.Select(b => (b.Group, b.Slot, b.Kind, b.VariableName, b.TypeName)).ToArray());
    }

    [Fact]
    public void MeshShader_MaterialFieldsComeFromTheMaterialStruct()
    {
        var shader = Shader.Load("Shaders/Mesh.wgsl");

        Assert.Equal(
            new[] { "color", "metallic", "roughness", "emissive" },
            shader.MaterialFields.Select(f => f.Name).ToArray());
        Assert.Equal("vec4<f32>", shader.MaterialFields[0].Type);
        Assert.All(shader.MaterialFields.Skip(1), field => Assert.Equal("f32", field.Type));

        // The material struct is what Material.Set validates against.
        Assert.Equal(shader.MaterialFields.Select(f => f.Name), shader.Parameters.Select(p => p.Name));
    }

    [Fact]
    public void PbrShader_ExposesTextureSlotsAndASampler()
    {
        var shader = Shader.Load("Shaders/Pbr.wgsl");

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
    public void UnlitShader_NeedsOnlyTheTransformInclude()
    {
        var shader = Shader.Load("Shaders/Unlit.wgsl");

        Assert.Single(shader.Techniques);
        Assert.Equal("Main", shader.Techniques[0].Name);
        Assert.Equal(
            new[] { "color", "metallic", "roughness", "emissive" },
            shader.MaterialFields.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void UnknownTechnique_Throws()
    {
        var shader = Shader.Load("Shaders/Mesh.wgsl");
        Assert.Throws<InvalidOperationException>(() => shader.GetTechnique("DoesNotExist"));
    }
}
