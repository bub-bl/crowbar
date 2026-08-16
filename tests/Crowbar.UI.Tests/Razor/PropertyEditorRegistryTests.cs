using Crowbar.UI;

namespace Crowbar.UI.Tests.Razor;

public class PropertyEditorRegistryTests
{
    [Fact]
    public void RegisterFromSourceExtractsTheDeclaredPropertyTypes()
    {
        // decimal is intentionally a type no editor handles, so registering it
        // cannot disturb the live editor mappings the page tests rely on. The
        // keyword alias resolves to the canonical full name.
        PropertyEditorRegistry.RegisterFromSource(
            "@attribute [EditorProperty(typeof(decimal))]\n" +
            "@attribute [EditorProperty(typeof(TimeSpan))]",
            "DemoEditor");

        Assert.Equal("DemoEditor", PropertyEditorRegistry.ResolveTag("System.Decimal"));
        Assert.Equal("DemoEditor", PropertyEditorRegistry.ResolveTag("System.TimeSpan"));
    }

    [Fact]
    public void UnknownTypesFallBackToTheObjectEditor()
    {
        Assert.Equal("ObjectEditor", PropertyEditorRegistry.ResolveTag("Some.Namespace.UnknownType"));
    }
}
