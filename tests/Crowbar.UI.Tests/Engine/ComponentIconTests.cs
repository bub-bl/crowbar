using System.Reflection;

namespace Crowbar.Engine.Tests;

public class ComponentIconTests
{
    [Theory]
    [InlineData(typeof(DirectionalLight), "Solar/devices/Bold/lightbulb")]
    [InlineData(typeof(PointLight), "Solar/devices/Bold/lightbulb")]
    [InlineData(typeof(MeshRenderer), "Solar/ui/Bold/box-minimalistic")]
    [InlineData(typeof(Camera), "Solar/video/Bold/camera")]
    public void Components_DeclareTheirEditorIconThroughTheAttribute(Type componentType, string iconPath)
    {
        // Resolved with inherit: true, exactly like the inspector does — the
        // lightbulb lives on the Light base class and covers every light.
        var attribute = componentType.GetCustomAttribute<ComponentIconAttribute>(inherit: true);

        Assert.NotNull(attribute);
        Assert.Equal(iconPath, attribute.Path);
    }

    [Fact]
    public void UndeclaredComponents_FallBackToTheDefaultIcon()
    {
        // The builders fall back to the shared default for components without
        // an attribute, so the constant is the single source of truth.
        Assert.Equal("Solar/ui/Bold/box", ComponentIconAttribute.DefaultPath);
    }
}
