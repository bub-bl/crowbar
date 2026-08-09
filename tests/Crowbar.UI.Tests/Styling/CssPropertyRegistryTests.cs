using Crowbar.UI;

namespace Crowbar.UI.Tests.Styling;

public class CssPropertyRegistryTests
{
    private const string CustomPropertyName = "__test-custom-length";

    [Fact]
    public void CustomPropertyCascadesThroughStyleSheet()
    {
        // Register a property aliased to an existing computed-style slot: the
        // whole cascade (parse -> apply -> read) works with a single registration.
        CssProperties.Register(CssProperties.Length(CustomPropertyName, s => s.Width, (s, v) => s.Width = v));

        var panel = new Panel();
        panel.AddClass("card");
        var style = StyleSheet.Parse($".card {{ {CustomPropertyName}: 42px; }}").Compute(panel);

        Assert.True(CssProperties.TryGet(CustomPropertyName, out var property));
        Assert.Equal(CssLength.Points(42), property!.GetValue(style));
        Assert.Equal(CssLength.Points(42), style.Width);
    }

    [Fact]
    public void UnknownPropertyIsIgnoredByApply()
    {
        var style = new ComputedStyle();
        Assert.False(CssProperties.TryApply(style, "definitely-not-a-property", "12px"));
        Assert.Equal(CssLength.Undefined, style.Width);
    }

    [Fact]
    public void DuplicateRegistrationThrows()
    {
        Assert.Throws<InvalidOperationException>(() => CssProperties.Register(
            CssProperties.Number("opacity", s => s.Opacity, (s, v) => s.Opacity = v, 1)));
    }

    [Fact]
    public void BuiltInPropertiesCarryTheirMetadata()
    {
        Assert.True(CssProperties.TryGet("background-color", out var color));
        Assert.True(color!.Animatable);
        Assert.False(color.Inherited);

        Assert.True(CssProperties.TryGet("font-size", out var fontSize));
        Assert.True(fontSize!.Inherited);

        Assert.True(CssProperties.TryGet("width", out _));
        Assert.True(CssProperties.TryGet("margin", out _));
        Assert.True(CssProperties.TryGet("transition", out _));
    }

    [Fact]
    public void AnimatablePropertiesLerp()
    {
        Assert.True(CssProperties.TryGet("opacity", out var opacity));
        var mid = Assert.IsType<float>(opacity!.Lerp(0f, 1f, 0.5f));
        Assert.Equal(0.5f, mid, 3);

        Assert.True(CssProperties.TryGet("background-color", out var color));
        var midColor = Assert.IsType<UiColor>(color!.Lerp(new UiColor(0, 0, 0, 255), new UiColor(255, 255, 255, 255), 0.5f));
        Assert.Equal(new UiColor(128, 128, 128, 255), midColor);
    }

    [Fact]
    public void NonAnimatablePropertyDoesNotLerp()
    {
        Assert.True(CssProperties.TryGet("z-index", out var zIndex));
        Assert.Null(zIndex!.Lerp(1, 2, 0.5f));
    }

    [Fact]
    public void LengthsLerpWithinSameUnitButNotAcrossUnits()
    {
        Assert.True(CssProperties.TryGet("width", out var width));
        Assert.Equal(CssLength.Points(15), Assert.IsType<CssLength>(width!.Lerp(CssLength.Points(10), CssLength.Points(20), 0.5f)));
        // Different units (or keywords like auto) fall back to discrete animation.
        Assert.Null(width.Lerp(CssLength.Points(10), CssLength.Auto, 0.5f));
    }

    [Fact]
    public void PanelInterpolatesAnimatablePropertiesOverTime()
    {
        using var ui = TestUi.Create();
        var panel = new Panel { TagName = "div" };
        panel.SetInlineStyle("width", "100px");
        panel.SetInlineStyle("height", "40px");
        panel.SetInlineStyle("transition", "background-color 0.2s");
        panel.SetInlineStyle("background-color", "#ff0000");
        ui.Screen.AddChild(panel);

        ui.Render();
        Assert.Equal(new UiColor(255, 0, 0, 255), panel.ComputedStyle.BackgroundColor);

        // The property change starts a transition: the style is interpolated
        // instead of snapping to the target.
        panel.SetInlineStyle("background-color", "#0000ff");
        ui.Render();
        Assert.Equal(new UiColor(255, 0, 0, 255), panel.ComputedStyle.BackgroundColor);

        // Halfway through the transition with the CSS `ease` curve
        // (cubic-bezier(0.25, 0.1, 0.25, 1)) the progress is ~0.803, not 0.5.
        ui.Update(0.1f);
        Assert.Equal(new UiColor(50, 0, 205, 255), panel.ComputedStyle.BackgroundColor);

        ui.Update(0.1f);
        Assert.Equal(new UiColor(0, 0, 255, 255), panel.ComputedStyle.BackgroundColor);
    }

    [Fact]
    public void ResetRestoresDefaults()
    {
        var style = new ComputedStyle
        {
            Width = CssLength.Points(100),
            Opacity = 0.5f,
            BackgroundColor = new UiColor(255, 0, 0, 255)
        };
        CssProperties.Reset(style);
        Assert.Equal(CssLength.Undefined, style.Width);
        Assert.Equal(1f, style.Opacity);
        Assert.Equal(UiColor.Transparent, style.BackgroundColor);
    }
}
