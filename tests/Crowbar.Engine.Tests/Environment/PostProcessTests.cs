using System.Numerics;
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
        component.Order = 2;

        using var loadedWorld = new World();
        var loaded = LevelSerializer.CreateLevel(
            loadedWorld,
            LevelSerializer.Deserialize(LevelSerializer.Serialize(source)));

        var loadedComponent = Assert.Single(loaded.Entities).GetComponent<Tonemapping>();
        Assert.NotNull(loadedComponent);
        Assert.Equal(TonemapOperator.Agx, loadedComponent!.Operator);
        Assert.Equal(1.5f, loadedComponent.Exposure);
        Assert.Equal(2, loadedComponent.Order);
        Assert.False(loaded.IsDirty);
    }

    [Fact]
    public void Tonemapping_DefaultsAreAcesWithNeutralExposure()
    {
        using var world = new World();
        var level = world.CreateLevel("PostProcess");
        var component = world.SpawnEntity("PostProcess", level).AddComponent<Tonemapping>();

        Assert.Equal(TonemapOperator.Aces, component.Operator);
        Assert.Equal(0f, component.Exposure);
        Assert.Equal(0, component.Order);
    }

    [Fact]
    public void Vignette_DefaultsAreIndustrySane()
    {
        using var world = new World();
        var level = world.CreateLevel("PostProcess");
        var component = world.SpawnEntity("PostProcess", level).AddComponent<Vignette>();

        Assert.Equal(0.4f, component.Intensity);
        Assert.Equal(0.65f, component.Radius);
        Assert.Equal(100, component.Order);
    }

    [Fact]
    public void VignetteShader_UsesViewportAwareUniforms()
    {
        var shader = Shader.Load("Shaders/PostProcesses/Vignette.wgsl");
        var uniforms = Assert.Single(shader.Structs, structure => structure.Name == "VignetteUniforms");

        Assert.Contains(uniforms.Fields, field => field.Name == "intensity");
        Assert.Contains(uniforms.Fields, field => field.Name == "radius");
        Assert.Contains(uniforms.Fields, field => field.Name == "viewportSize");
        Assert.Equal(16, UniformPacker.ComputeStructSize(uniforms.Fields));
    }

    [Fact]
    public void ChromaticAberration_PropertiesRoundTrip()
    {
        using var sourceWorld = new World();
        var source = sourceWorld.CreateLevel("PostProcess");
        var component = sourceWorld.SpawnEntity("PostProcess", source).AddComponent<ChromaticAberration>();
        component.Intensity = 0.35f;
        component.Start = 0.25f;
        component.Order = 75;

        using var loadedWorld = new World();
        var loaded = LevelSerializer.CreateLevel(loadedWorld, LevelSerializer.Deserialize(LevelSerializer.Serialize(source)));
        var loadedComponent = Assert.Single(loaded.Entities).GetComponent<ChromaticAberration>();
        Assert.NotNull(loadedComponent);
        Assert.Equal(0.35f, loadedComponent!.Intensity);
        Assert.Equal(0.25f, loadedComponent.Start);
        Assert.Equal(75, loadedComponent.Order);
    }

    [Fact]
    public void ChromaticAberration_DefaultsAreNeutral()
    {
        var component = new ChromaticAberration();
        Assert.Equal(0f, component.Intensity);
        Assert.Equal(0.5f, component.Start);
        Assert.Equal(50, component.Order);
        Assert.Equal(typeof(ChromaticAberration), GlobalNamespaces.TypeLibrary.Registry.Resolve("ChromaticAberration"));
    }

    [Fact]
    public void ChromaticAberrationShader_UsesViewportAwareUniforms()
    {
        var shader = Shader.Load("Shaders/PostProcesses/ChromaticAberration.wgsl");
        var uniforms = Assert.Single(shader.Structs, structure => structure.Name == "ChromaticAberrationUniforms");
        Assert.Contains(uniforms.Fields, field => field.Name == "intensity");
        Assert.Contains(uniforms.Fields, field => field.Name == "start");
        Assert.Contains(uniforms.Fields, field => field.Name == "viewportSize");
        Assert.Equal(16, UniformPacker.ComputeStructSize(uniforms.Fields));
    }

    [Fact]
    public void FilmGrain_PropertiesRoundTrip()
    {
        using var sourceWorld = new World();
        var source = sourceWorld.CreateLevel("PostProcess");
        var component = sourceWorld.SpawnEntity("PostProcess", source).AddComponent<FilmGrain>();
        component.Intensity = 0.3f;
        component.Response = 0.65f;
        component.Order = 175;

        using var loadedWorld = new World();
        var loaded = LevelSerializer.CreateLevel(loadedWorld, LevelSerializer.Deserialize(LevelSerializer.Serialize(source)));
        var loadedComponent = Assert.Single(loaded.Entities).GetComponent<FilmGrain>();
        Assert.NotNull(loadedComponent);
        Assert.Equal(0.3f, loadedComponent!.Intensity);
        Assert.Equal(0.65f, loadedComponent.Response);
        Assert.Equal(175, loadedComponent.Order);
    }

    [Fact]
    public void FilmGrain_DefaultsAreNeutral()
    {
        var component = new FilmGrain();
        Assert.Equal(0f, component.Intensity);
        Assert.Equal(0.8f, component.Response);
        Assert.Equal(150, component.Order);
        Assert.Equal(typeof(FilmGrain), GlobalNamespaces.TypeLibrary.Registry.Resolve("FilmGrain"));
    }

    [Fact]
    public void FilmGrainShader_ExposesTemporalUniforms()
    {
        var shader = Shader.Load("Shaders/PostProcesses/FilmGrain.wgsl");
        var uniforms = Assert.Single(shader.Structs, structure => structure.Name == "FilmGrainUniforms");
        Assert.Contains(uniforms.Fields, field => field.Name == "intensity");
        Assert.Contains(uniforms.Fields, field => field.Name == "response");
        Assert.Contains(uniforms.Fields, field => field.Name == "time");
        Assert.Contains(uniforms.Fields, field => field.Name == "viewportSize");
        Assert.Equal(32, UniformPacker.ComputeStructSize(uniforms.Fields));
    }

    [Fact]
    public void WorldQuery_ReturnsEveryPostProcessInstanceOnAnEntity()
    {
        // The camera carries Tonemapping + Vignette on one entity; the chain
        // is built from world.Query<PostProcess>(), which must yield every
        // instance, not just the first like Entity.GetComponent<T>.
        using var world = new World();
        var entity = world.SpawnEntity("Camera");
        entity.AddComponent<Tonemapping>();
        entity.AddComponent<TestBlendable>();

        var found = world.Query<PostProcess>().ToList();
        Assert.Equal(2, found.Count);
        Assert.Single(found.OfType<Tonemapping>());
        Assert.Single(found.OfType<TestBlendable>());
    }

    [Fact]
    public void PostProcess_HierarchyIsPublicAndExtensible()
    {
        // The whole API is public: game-project code derives from it (the
        // demo project ships a Vignette effect).
        Assert.True(typeof(PostProcess).IsPublic);
        Assert.True(typeof(PostProcess).IsAbstract);
        Assert.True(typeof(PostProcess).IsSubclassOf(typeof(Component)));
        Assert.True(typeof(BasePostProcess<>).IsPublic);
        Assert.True(typeof(BasePostProcess<>).IsAbstract);
        Assert.True(typeof(SinglePassPostProcess).IsPublic);
        Assert.True(typeof(SinglePassPostProcess).IsAbstract);

        // Tonemapping is the CRTP volume-blended flavor; the public API
        // surface (Render + context) is what a game effect overrides.
        Assert.Equal(typeof(BasePostProcess<Tonemapping>), typeof(Tonemapping).BaseType);
        Assert.False(typeof(Tonemapping).IsAbstract);
        Assert.NotNull(typeof(Tonemapping).GetMethod("Render"));
        Assert.Equal(typeof(PostProcessContext), typeof(Tonemapping).GetMethod("Render")!.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void TonemappingShader_ExposesTypedUniformFields()
    {
        var shader = Shader.Load("Shaders/PostProcesses/Tonemapping.wgsl");

        var uniforms = Assert.Single(shader.Structs, structure => structure.Name == "TonemappingUniforms");
        Assert.Contains(uniforms.Fields, field => field.Name == "operator_");
        Assert.Contains(uniforms.Fields, field => field.Name == "exposure");
        Assert.Contains(shader.Bindings, binding => binding.VariableName == "sceneTexture" && binding.Slot == 0u);
        Assert.Contains(shader.Bindings, binding => binding.VariableName == "sceneSampler" && binding.Slot == 1u);
        Assert.Contains(shader.Bindings, binding =>
            binding.VariableName == "postProcess" && binding.Slot == 2u &&
            binding.Kind == ShaderBindingKind.UniformBuffer);
    }

    [Fact]
    public void TonemappingUniformStruct_IsPackedToTheWgslUniformSize()
    {
        // Two floats (8 bytes) must bind as a 16-byte uniform buffer:
        // WGSL uniform structs are 16-byte aligned, and wgpu validates the
        // bound size against the shader's expectation. Regression: the buffer
        // used to be created at 12 bytes, failing with "Buffer is bound with
        // size 12 where the shader expects 16".
        var shader = Shader.Load("Shaders/PostProcesses/Tonemapping.wgsl");
        var uniforms = Assert.Single(shader.Structs, structure => structure.Name == "TonemappingUniforms");

        Assert.Equal(16, UniformPacker.ComputeStructSize(uniforms.Fields));
    }

    [Fact]
    public void RenderAttributes_PackByNameMatchesBothSides()
    {
        // The matching must normalize the attribute key as well as the shader
        // field name: Tonemapping sets an explicit "operator_" attribute while
        // SinglePassPostProcess collects "Operator" — both must reach the
        // operator_ field. Regression: the attribute side was compared raw, so
        // operator_ never packed and the pass ran as identity ("nothing
        // happens").
        Assert.True(RenderAttributes.MatchesField("operator_", "Operator"));
        Assert.True(RenderAttributes.MatchesField("operator_", "operator_"));
        Assert.True(RenderAttributes.MatchesField("exposure", "Exposure"));
        Assert.False(RenderAttributes.MatchesField("radius", "intensity"));

        var shader = Shader.Load("Shaders/PostProcesses/Tonemapping.wgsl");
        var fields = Assert.Single(shader.Structs, structure => structure.Name == "TonemappingUniforms").Fields;

        // Mirrors Tonemapping.Render + the renderer's PackAttributes.
        var attributes = new RenderAttributes()
            .Set("operator_", 3f) // Agx
            .Set("exposure", 1f);
        var values = new Dictionary<string, ShaderParameter>();
        foreach (var field in fields)
        {
            foreach (var (name, parameter) in attributes.Values)
            {
                if (!RenderAttributes.MatchesField(field.Name, name))
                    continue;
                values[field.Name] = parameter;
                break;
            }
        }

        var packed = UniformPacker.Pack(fields, values);
        Assert.Equal(3f, BitConverter.ToSingle(packed, 0)); // operator_ at offset 0
        Assert.Equal(1f, BitConverter.ToSingle(packed, 4)); // exposure
    }

    [Fact]
    public void SinglePassPostProcess_CollectsPropertiesByName()
    {
        var component = new TestSinglePass { Brightness = 0.5f, Count = 3, Mode = TonemapOperator.Agx };
        var attributes = PostProcessAttributes.Collect(component);

        Assert.Equal(0.5f, AsFloat(attributes.Values["Brightness"]));
        Assert.Equal(3, AsInt(attributes.Values["Count"]));
        // Enums pack as their numeric value.
        Assert.Equal(3f, AsFloat(attributes.Values["Mode"]));
        // Non-[Property] members and base infrastructure (Order, Sampler) are excluded.
        Assert.DoesNotContain("NotAProperty", attributes.Values.Keys);
        Assert.DoesNotContain("Order", attributes.Values.Keys);
        Assert.DoesNotContain("Sampler", attributes.Values.Keys);
    }

    [Fact]
    public void PostProcessBlender_BlendsVolumeWeights()
    {
        var global = new Tonemapping { Exposure = 0f };
        var volumeA = new Tonemapping { Exposure = 1f };
        var volumeB = new Tonemapping { Exposure = 2f };
        PostProcessEntry[] entries =
        [
            new(global, 1f, IsGlobal: true),
            new(volumeA, 0.5f, IsGlobal: false),
            new(volumeB, 0.25f, IsGlobal: false)
        ];

        // (0·1 + 1·0.5 + 2·0.25) / (1 + 0.5 + 0.25) = 1 / 1.75
        var blended = PostProcessBlender.Blend(entries, global, effect => ((Tonemapping)effect).Exposure, 0f, false);
        Assert.Equal(1f / 1.75f, blended, precision: 5);

        // Without any volume the driver's own value is returned unchanged.
        var noVolumes = PostProcessBlender.Blend([new PostProcessEntry(global, 1f, IsGlobal: true)],
            global, effect => ((Tonemapping)effect).Exposure, 0f, false);
        Assert.Equal(0f, noVolumes, precision: 5);

        // onlyLerpBetweenVolumes ignores the global instance.
        var volumesOnly = PostProcessBlender.Blend(entries, global,
            effect => ((Tonemapping)effect).Exposure, 0f, onlyLerpBetweenVolumes: true);
        Assert.Equal(1f / 0.75f, volumesOnly, precision: 5);
    }

    [Fact]
    public void GetWeighted_BlendsThroughTheActiveContext()
    {
        var global = new TestBlendable { Brightness = 0f };
        var volume = new TestBlendable { Brightness = 1f };
        var context = new PostProcessContext(null!, null!, null!,
        [
            new PostProcessEntry(global, 1f, IsGlobal: true),
            new PostProcessEntry(volume, 1f, IsGlobal: false)
        ]);

        PostProcessContext.Current = context;
        try
        {
            Assert.Equal(0.5f, global.GetBrightness(), precision: 5);
        }
        finally
        {
            PostProcessContext.Current = null;
        }
    }

    [Fact]
    public void PostProcessVolume_WeightsTheCameraPosition()
    {
        using var world = new World();
        var level = world.CreateLevel("Volume");
        var entity = world.SpawnEntity("Volume", level);
        var volume = entity.AddComponent<PostProcessVolume>();
        volume.World = new Transform(Vector3.Zero, Rotation.Identity, new Vector3(2f, 2f, 2f));

        Assert.True(volume.TryGetWeight(Vector3.Zero, out var center));
        Assert.Equal(1f, center, precision: 5);

        // Deep inside (t = 0.5 <= 1 - softness): full weight.
        Assert.True(volume.TryGetWeight(new Vector3(0.5f, 0f, 0f), out var inner));
        Assert.Equal(1f, inner, precision: 5);

        // Near the boundary (t = 0.9): ramped by softness → (1 - 0.9) / 0.2.
        Assert.True(volume.TryGetWeight(new Vector3(0.9f, 0f, 0f), out var edge));
        Assert.Equal(0.5f, edge, precision: 5);

        // Outside: no weight.
        Assert.False(volume.TryGetWeight(new Vector3(1.1f, 0f, 0f), out _));
    }

    [Fact]
    public void Bloom_PropertiesRoundTrip()
    {
        using var sourceWorld = new World();
        var source = sourceWorld.CreateLevel("PostProcess");
        var entity = sourceWorld.SpawnEntity("PostProcess", source);
        var component = entity.AddComponent<Bloom>();
        component.Intensity = 0.9f;
        component.Threshold = 1.2f;
        component.ThresholdKnee = 0.4f;
        component.Scatter = 0.6f;
        component.DownsampleCount = 3;
        component.Clamp = 2.5f;
        component.Order = 4;

        using var loadedWorld = new World();
        var loaded = LevelSerializer.CreateLevel(
            loadedWorld,
            LevelSerializer.Deserialize(LevelSerializer.Serialize(source)));

        var loadedComponent = Assert.Single(loaded.Entities).GetComponent<Bloom>();
        Assert.NotNull(loadedComponent);
        Assert.Equal(0.9f, loadedComponent!.Intensity);
        Assert.Equal(1.2f, loadedComponent.Threshold);
        Assert.Equal(0.4f, loadedComponent.ThresholdKnee);
        Assert.Equal(0.6f, loadedComponent.Scatter);
        Assert.Equal(3, loadedComponent.DownsampleCount);
        Assert.Equal(2.5f, loadedComponent.Clamp);
        Assert.Equal(4, loadedComponent.Order);
        Assert.False(loaded.IsDirty);
    }

    [Fact]
    public void Bloom_DefaultsAreSane()
    {
        using var world = new World();
        var level = world.CreateLevel("PostProcess");
        var component = world.SpawnEntity("PostProcess", level).AddComponent<Bloom>();

        Assert.Equal(1f, component.Intensity);
        Assert.Equal(1f, component.Threshold);
        Assert.Equal(0.5f, component.ThresholdKnee);
        Assert.Equal(0.7f, component.Scatter);
        Assert.Equal(4, component.DownsampleCount);
        Assert.Equal(3.5f, component.Clamp);
        Assert.Equal(-100, component.Order);
    }

    [Fact]
    public void Bloom_ResolvesByShortName()
    {
        // Moving a demo effect into the engine exposes it to the level format's
        // short-name component index and the editor's Add Component list.
        var type = GlobalNamespaces.TypeLibrary.Registry.Resolve("Bloom");
        Assert.Equal(typeof(Bloom), type);
    }

    [Fact]
    public void BloomCombineShader_ExposesTypedUniformFieldAndSecondInput()
    {
        var shader = Shader.Load("Shaders/PostProcesses/BloomCombine.wgsl");

        var uniforms = Assert.Single(shader.Structs, structure => structure.Name == "BloomCombineUniforms");
        Assert.Contains(uniforms.Fields, field => field.Name == "intensity");
        // The final combine reads two inputs: scene at slot 0 and glow at slot 3.
        Assert.Contains(shader.Bindings, binding => binding.VariableName == "sceneTexture" && binding.Slot == 0u);
        Assert.Contains(shader.Bindings, binding => binding.VariableName == "glowTexture" && binding.Slot == 3u);
        Assert.Contains(shader.Bindings, binding => binding.VariableName == "glowSampler" && binding.Slot == 4u);
        Assert.Contains(shader.Bindings, binding =>
            binding.VariableName == "postProcess" && binding.Kind == ShaderBindingKind.UniformBuffer);
    }

    [Fact]
    public void BloomPrefilterUniformStruct_IsPackedToTheWgslUniformSize()
    {
        var shader = Shader.Load("Shaders/PostProcesses/BloomPrefilter.wgsl");
        var uniforms = Assert.Single(shader.Structs, structure => structure.Name == "BloomPrefilterUniforms");
        Assert.Contains(uniforms.Fields, field => field.Name == "threshold");
        Assert.Contains(uniforms.Fields, field => field.Name == "thresholdKnee");
        Assert.Contains(uniforms.Fields, field => field.Name == "clamp_");

        Assert.Equal(16, UniformPacker.ComputeStructSize(uniforms.Fields));
    }

    [Fact]
    public void BloomDownsampleUniformStruct_IsPackedToTheWgslUniformSize()
    {
        var shader = Shader.Load("Shaders/PostProcesses/BloomDownsample.wgsl");
        var uniforms = Assert.Single(shader.Structs, structure => structure.Name == "BloomDownsampleUniforms");
        Assert.Contains(uniforms.Fields, field => field.Name == "invTexel");

        Assert.Equal(16, UniformPacker.ComputeStructSize(uniforms.Fields));
    }

    private static float AsFloat(ShaderParameter parameter) =>
        parameter is float value ? value : throw new InvalidOperationException($"Expected a float uniform, got {parameter.TypeName}.");

    private static int AsInt(ShaderParameter parameter) =>
        parameter is int value ? value : throw new InvalidOperationException($"Expected an int uniform, got {parameter.TypeName}.");

    private sealed class TestSinglePass : SinglePassPostProcess
    {
        [Property]
        public float Brightness { get; set; }

        [Property]
        public int Count { get; set; }

        [Property]
        public TonemapOperator Mode { get; set; }

        public string NotAProperty { get; set; } = "ignored";

        public override string ShaderPath => "Shaders/PostProcesses/Tonemapping.wgsl";
    }

    private sealed class TestBlendable : BasePostProcess<TestBlendable>
    {
        [Property]
        public float Brightness { get; set; }

        public float GetBrightness() => GetWeighted(effect => effect.Brightness);

        public override void Render(PostProcessContext context)
        {
        }
    }
}
