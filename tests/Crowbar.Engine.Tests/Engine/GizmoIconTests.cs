using System.Reflection;

namespace Crowbar.Engine.Tests;

public class GizmoIconTests
{
    [Theory]
    [InlineData(typeof(DirectionalLight), "directional-light")]
    [InlineData(typeof(PointLight), "point-light")]
    [InlineData(typeof(MeshRenderer), "mesh")]
    [InlineData(typeof(Camera), "camera")]
    public void Components_DeclareTheirGizmoIconThroughTheAttribute(Type componentType, string iconName)
    {
        var attribute = componentType.GetCustomAttribute<GizmoIconAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(iconName, attribute.Name);

        // The icon asset must exist next to the engine output.
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Gizmos", $"{iconName}.svg");
        Assert.True(File.Exists(path), $"Expected gizmo icon asset '{path}' to be shipped.");
    }
}
