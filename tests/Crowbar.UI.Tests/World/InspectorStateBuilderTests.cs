using System.Numerics;
using Crowbar.UI;

namespace Crowbar.Engine.Tests;

public class InspectorStateBuilderTests
{
    [Fact]
    public void ReflectsMarkedPropertiesIntoEditors()
    {
        using var world = new World();
        var light = world.SpawnEntity("Fill Light");
        var point = light.AddComponent<PointLight>();
        point.Local = new Transform(new Vector3(1, 2, 3), Rotation.FromYaw(30f), new Vector3(2f));
        point.Color = new Vector3(0.4f, 0.6f, 1f);
        point.Intensity = 4f;
        point.Range = 8f;

        var sections = InspectorStateBuilder.Build(light);

        // The transform is a dedicated section with the three vector rows.
        var transform = sections.Single(s => s.Id == "transform");
        Assert.Equal("Transform", transform.Title);
        Assert.Equal(new[] { "Position", "Rotation", "Scale" }, transform.Properties.Select(p => p.Name).ToArray());
        Assert.All(transform.Properties, p => Assert.Equal("System.Numerics.Vector3", p.TypeName));

        // The component section resolves each marked property by its CLR type.
        var section = sections.Single(s => s.Id == "PointLight");
        var byName = section.Properties.ToDictionary(p => p.Name);

        Assert.Equal("System.Numerics.Vector3", byName["Color"].TypeName);
        Assert.Equal("0.4, 0.6, 1", byName["Color"].Value);

        Assert.Equal("System.Single", byName["Intensity"].TypeName);
        Assert.Equal("4", byName["Intensity"].Value);

        Assert.Equal("System.Single", byName["Range"].TypeName);
        Assert.Equal("8", byName["Range"].Value);
    }

    [Fact]
    public void MaterialExpandsItsShaderParametersDynamically()
    {
        using var world = new World();
        var cube = world.SpawnEntity("Cube");
        var mesh = cube.AddComponent<MeshRenderer>();
        mesh.Model = Model.CreateCube();
        mesh.Material = Material.FromShader("Surface/StandardPbr")
            .Set("color", new Vector4(0.2f, 0.6f, 1.0f, 1.0f))
            .Set("metallic", 0.15f);

        var section = InspectorStateBuilder.Build(cube).Single(s => s.Id == "MeshRenderer");

        var material = section.Properties.Single(p => p.Name == "Material");
        Assert.Equal("Crowbar.Engine.Material", material.TypeName);
        Assert.Equal("StandardPbr", material.Value);

        // Shader parameters become nested rows, each resolved by its own type.
        var color = section.Properties.Single(p => p.Name == "Color");
        Assert.Equal("System.Numerics.Vector4", color.TypeName);
        Assert.Equal("0.2, 0.6, 1, 1", color.Value);
        Assert.Equal(1, color.Indent);

        var metallic = section.Properties.Single(p => p.Name == "Metallic");
        Assert.Equal("System.Single", metallic.TypeName);
        Assert.Equal("0.15", metallic.Value);
        Assert.Equal(1, metallic.Indent);

        var model = section.Properties.Single(p => p.Name == "Model");
        Assert.Equal("Crowbar.Engine.Model", model.TypeName);
        Assert.Equal("Cube", model.Value);
    }

    [Fact]
    public void UnmarkedPropertiesAreNotInspected()
    {
        using var world = new World();
        var cube = world.SpawnEntity("Cube");
        cube.AddComponent<MeshRenderer>();

        var section = InspectorStateBuilder.Build(cube).Single(s => s.Id == "MeshRenderer");

        // Lifecycle/attachment plumbing (Entity, Enabled, TickEnabled, Local,
        // World, Parent, ...) must never appear in the inspector.
        Assert.DoesNotContain(section.Properties, p => p.Name is "Entity" or "Enabled" or "TickEnabled" or "Local" or "World");
    }

    [Fact]
    public void ApplyEditWritesBackToTransformComponentAndMaterial()
    {
        using var world = new World();
        var cube = world.SpawnEntity("Cube");
        var mesh = cube.AddComponent<MeshRenderer>();
        mesh.Model = Model.CreateCube();
        mesh.Material = Material.FromShader("Surface/StandardPbr").Set("metallic", 0.15f);
        mesh.Local = new Transform(Vector3.Zero, Rotation.Identity, Vector3.One);

        // A material shader parameter is resolved through the Material key.
        InspectorStateBuilder.ApplyEdit(cube, "MeshRenderer.Material.metallic", "0.9");
        Assert.True(Math.Abs(mesh.Material!.Get<float>("metallic") - 0.9f) < 0.0001f);

        // A transform field is applied to the component's local transform.
        InspectorStateBuilder.ApplyEdit(cube, "transform.position", "1, 2, 3");
        Assert.Equal(new Vector3(1, 2, 3), mesh.Local.Position);

        // Malformed values are ignored, so a bad keystroke never crashes the host.
        InspectorStateBuilder.ApplyEdit(cube, "transform.position", "not-a-vector");
        InspectorStateBuilder.ApplyEdit(cube, "Unknown.Type", "5");
        Assert.Equal(new Vector3(1, 2, 3), mesh.Local.Position);
    }
}
