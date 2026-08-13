using System.Globalization;
using Crowbar.UI;

namespace Crowbar.UI.Tests.Styling;

public class FilterTests
{
    static FilterTests()
    {
        // Registers the custom "tint" filter used by the extensibility tests.
        CssFilterFunctions.Register(new TestTintFilter());
    }

    [Fact]
    public void ParsesFilterListInOrder()
    {
        Assert.True(CssFilterFunctions.TryParse("blur(4px) contrast(200%)", out var filter));
        Assert.False(filter.IsNone);
        Assert.Equal(2, filter.Functions.Count);
        Assert.Equal("blur", filter.Functions[0].Name);
        Assert.Equal(4f, filter.Functions[0].Parameters[0]);
        Assert.Equal("contrast", filter.Functions[1].Name);
        Assert.Equal(2f, filter.Functions[1].Parameters[0]);
    }

    [Fact]
    public void NoneParsesToEmptyList()
    {
        Assert.True(CssFilterFunctions.TryParse("none", out var filter));
        Assert.True(filter.IsNone);
        Assert.Same(CssFilter.None, filter);
    }

    [Fact]
    public void InvalidFilterValueIsRejected()
    {
        Assert.False(CssFilterFunctions.TryParse("blur()", out _));           // missing argument
        Assert.False(CssFilterFunctions.TryParse("blur(4px 5px)", out _));    // too many arguments
        Assert.False(CssFilterFunctions.TryParse("xray(2)", out _));          // unknown function
        Assert.False(CssFilterFunctions.TryParse("brightness", out _));       // missing parentheses
        Assert.False(CssFilterFunctions.TryParse("blur(4px) xray(1)", out _)); // one bad function poisons the list
        Assert.False(CssFilterFunctions.TryParse("", out _));
    }

    [Fact]
    public void PercentagesConvertToFractions()
    {
        Assert.True(CssFilterFunctions.TryParse("brightness(150%)", out var filter));
        Assert.Equal(1.5f, filter.Functions[0].Parameters[0]);
    }

    [Fact]
    public void AmountsAreClamped()
    {
        Assert.True(CssFilterFunctions.TryParse("grayscale(200%)", out var grayscale));
        Assert.Equal(1f, grayscale.Functions[0].Parameters[0]);
        Assert.True(CssFilterFunctions.TryParse("invert(-1)", out var invert));
        Assert.Equal(0f, invert.Functions[0].Parameters[0]);
        Assert.True(CssFilterFunctions.TryParse("brightness(-2)", out var brightness));
        Assert.Equal(0f, brightness.Functions[0].Parameters[0]);
        // Brightness/contrast/saturate may exceed 100%.
        Assert.True(CssFilterFunctions.TryParse("saturate(300%)", out var saturate));
        Assert.Equal(3f, saturate.Functions[0].Parameters[0]);
    }

    [Fact]
    public void DropShadowParsesOffsetsBlurAndColor()
    {
        Assert.True(CssFilterFunctions.TryParse("drop-shadow(2px 3px 4px rgba(10, 20, 30, 0.5))", out var filter));
        var shadow = filter.Functions[0];
        Assert.Equal(2f, shadow.Parameters[0]);
        Assert.Equal(3f, shadow.Parameters[1]);
        Assert.Equal(4f, shadow.Parameters[2]);
        Assert.Equal(new UiColor(10, 20, 30, 128), shadow.Color);
    }

    [Fact]
    public void DropShadowAcceptsNegativeOffsets()
    {
        Assert.True(CssFilterFunctions.TryParse("drop-shadow(-2px -3px 4px #000000)", out var filter));
        Assert.Equal(-2f, filter.Functions[0].Parameters[0]);
        Assert.Equal(-3f, filter.Functions[0].Parameters[1]);
        Assert.Equal(4f, filter.Functions[0].Parameters[2]);
    }

    [Fact]
    public void DropShadowDefaultsToBlackWhenColorOmitted()
    {
        Assert.True(CssFilterFunctions.TryParse("drop-shadow(1px 1px)", out var filter));
        Assert.Equal(new UiColor(0, 0, 0, 255), filter.Functions[0].Color);
        Assert.Equal(0f, filter.Functions[0].Parameters[2]);
    }

    [Fact]
    public void DropShadowAcceptsModernColorSyntax()
    {
        Assert.True(CssFilterFunctions.TryParse("drop-shadow(0 0 4px rgb(0 0 0 / 50%))", out var filter));
        Assert.Equal(new UiColor(0, 0, 0, 128), filter.Functions[0].Color);
    }

    [Fact]
    public void HueRotateAcceptsAngleUnits()
    {
        Assert.True(CssFilterFunctions.TryParse("hue-rotate(180deg)", out var deg));
        Assert.Equal(180f, deg.Functions[0].Parameters[0], 3);
        Assert.True(CssFilterFunctions.TryParse("hue-rotate(0.5turn)", out var turn));
        Assert.Equal(180f, turn.Functions[0].Parameters[0], 3);
        Assert.True(CssFilterFunctions.TryParse("hue-rotate(3.14159265rad)", out var rad));
        Assert.Equal(180f, rad.Functions[0].Parameters[0], 1);
    }

    [Fact]
    public void FilterAndBackdropFilterCascadeThroughStyleSheet()
    {
        var panel = new Panel();
        panel.AddClass("glass");
        var style = StyleSheet.Parse(".glass { filter: blur(3px); backdrop-filter: brightness(0.8) saturate(1.2); }").Compute(panel);

        Assert.False(style.Filter.IsNone);
        Assert.Equal("blur", style.Filter.Functions[0].Name);
        Assert.Equal(3f, style.Filter.Functions[0].Parameters[0]);
        Assert.Equal(2, style.BackdropFilter.Functions.Count);
        Assert.Equal("brightness", style.BackdropFilter.Functions[0].Name);
        Assert.Equal("saturate", style.BackdropFilter.Functions[1].Name);
    }

    [Fact]
    public void InvalidFilterPropertyIsIgnored()
    {
        var style = new ComputedStyle();
        Assert.False(CssProperties.TryApply(style, "filter", "xray(2)"));
        Assert.True(style.Filter.IsNone);
        Assert.False(CssProperties.TryApply(style, "backdrop-filter", "blur()"));
        Assert.True(style.BackdropFilter.IsNone);
    }

    [Fact]
    public void CustomFilterFunctionParses()
    {
        Assert.True(CssFilterFunctions.TryParse("tint(0.5)", out var filter));
        var function = filter.Functions[0];
        Assert.Equal("tint", function.Name);
        Assert.Equal(0.5f, function.Parameters[0]);
    }

    [Fact]
    public void DuplicateFilterRegistrationThrows()
    {
        Assert.Throws<InvalidOperationException>(() => CssFilterFunctions.Register(new TestTintFilter()));
    }

    [Fact]
    public void FilterEqualityIsStructural()
    {
        Assert.True(CssFilterFunctions.TryParse("blur(4px)", out var a));
        Assert.True(CssFilterFunctions.TryParse("blur(4px)", out var b));
        Assert.True(CssFilterFunctions.TryParse("blur(8px)", out var c));
        Assert.True(CssFilterFunctions.TryParse("blur(4px) contrast(2)", out var d));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void BuiltInFilterFunctionsAreRegistered()
    {
        foreach (var name in new[] { "blur", "brightness", "contrast", "drop-shadow", "grayscale", "hue-rotate", "invert", "opacity", "saturate", "sepia" })
            Assert.True(CssFilterFunctions.TryGet(name, out _), $"filter function '{name}' should be registered");
    }

    /// <summary>A trivial custom filter for the extensibility tests.</summary>
    internal sealed class TestTintFilter : FilterFunctionDefinition
    {
        public TestTintFilter() : base("tint") { }

        public override bool TryParse(string arguments, out CssFilterFunction function)
        {
            function = null!;
            if (!float.TryParse(arguments, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return false;
            function = new CssFilterFunction(Name, [amount]);
            return true;
        }
    }
}
