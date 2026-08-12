using Crowbar.UI;
using SkiaSharp;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// The <c>&lt;icon&gt;</c> raster comes from the SVG pack in
/// <c>Assets/Icons</c> and is tinted with the panel's computed <c>color</c>
/// (both <c>currentColor</c> and the Solar pack's hardcoded hex fills are
/// normalized to the tint). These tests verify the rasterization and the
/// raster output from the raw RGBA buffer.
/// </summary>
public class IconRenderTests : IDisposable
{
    private readonly TempDirectory _tmp = TestUi.TempDir("icons");
    private readonly UiSystem _ui = TestUi.Create(60, 60);
    private readonly SvgIconCache _cache = new();

    public IconRenderTests()
    {
        _cache.ContentRoot = _tmp.Path;
        _ui.Renderer.IconCache = _cache;
        // A Crowbar-style icon: fill comes from currentColor.
        _tmp.Write("square.svg", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect x="2" y="2" width="20" height="20" fill="currentColor"/></svg>
            """);
        // A Solar-style icon: the fill is a hardcoded hex (must be tinted too).
        Directory.CreateDirectory(Path.Combine(_tmp.Path, "Solar", "ui", "Bold"));
        _tmp.Write("Solar/ui/Bold/cursor.svg", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M4 3l16 8-6 2-2 6z" fill="#1C274C"/></svg>
            """);
    }

    public void Dispose()
    {
        _ui.Dispose();
        _cache.Clear();
        _tmp.Dispose();
    }

    private static Icon IconAt(string name, int x, int y, string color)
    {
        var icon = new Icon { Name = name };
        icon.SetInlineStyle("width", "20px");
        icon.SetInlineStyle("height", "20px");
        icon.SetInlineStyle("color", color);
        icon.SetInlineStyle("position", "absolute");
        icon.SetInlineStyle("left", $"{x}px");
        icon.SetInlineStyle("top", $"{y}px");
        return icon;
    }

    private static (byte R, byte G, byte B, byte A) Pixel(byte[] buffer, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return (buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]);
    }

    [Fact]
    public void IconIsTintedWithTheComputedColor()
    {
        _ui.Screen.AddChild(IconAt("square", 20, 20, "rgb(255, 80, 40)"));
        var pixels = _ui.Render().ToArray();

        var center = Pixel(pixels, 60, 30, 30);
        Assert.True(center.A > 200 && center.R > 200 && center.G is > 50 and < 110 && center.B < 70,
            $"icon center should be tinted rgb(255,80,40), got {center}");
    }

    [Fact]
    public void SolarHexFillIsTintedInsteadOfThePackColor()
    {
        // The Solar pack hardcodes #1C274C; the raster must use the computed
        // color instead, or the icon would be invisible on the dark theme.
        _ui.Screen.AddChild(IconAt("Solar/ui/Bold/cursor", 20, 20, "rgb(255, 80, 40)"));
        var pixels = _ui.Render().ToArray();

        var center = Pixel(pixels, 60, 30, 30);
        Assert.True(center.A > 200 && center.R > 200 && center.G is > 50 and < 110 && center.B < 70,
            $"Solar icon center should be tinted rgb(255,80,40) not #1C274C, got {center}");
    }

    [Fact]
    public void TwoIconsTintIndependently()
    {
        _ui.Screen.AddChild(IconAt("square", 10, 10, "rgb(255, 0, 0)"));
        _ui.Screen.AddChild(IconAt("square", 30, 30, "rgb(0, 0, 255)"));
        var pixels = _ui.Render().ToArray();

        var red = Pixel(pixels, 60, 20, 20);
        var blue = Pixel(pixels, 60, 40, 40);
        Assert.True(red.R > 200 && red.B < 70, $"first icon should be red, got {red}");
        Assert.True(blue.B > 200 && blue.R < 70, $"second icon should be blue, got {blue}");
    }

    [Fact]
    public void MissingIconDrawsNothing()
    {
        _ui.Screen.AddChild(IconAt("does-not-exist", 20, 20, "rgb(255, 0, 0)"));
        var pixels = _ui.Render().ToArray();

        var center = Pixel(pixels, 60, 30, 30);
        Assert.True(center.A < 20, $"missing icon must draw nothing, got {center}");
    }

    [Fact]
    public void TraversalNamesAreRejected()
    {
        var cache = new SvgIconCache { ContentRoot = _tmp.Path };
        Assert.Null(cache.Get("../../secret", SKColors.Red, 16, 16));
        Assert.Null(cache.Get("/absolute", SKColors.Red, 16, 16));
        cache.Clear();
    }
}
