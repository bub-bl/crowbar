using Crowbar.UI;

namespace Crowbar.UI.Tests.Layout;

/// <summary>
/// An <c>&lt;icon&gt;</c> without explicit dimensions sizes to its intrinsic
/// SVG ratio at a 16x16 default; with one axis fixed, the other follows the
/// ratio (icons are usually square).
/// </summary>
public class IconLayoutTests
{
    private static (TempDirectory Tmp, SvgIconCache Cache) Fixture()
    {
        var tmp = TestUi.TempDir("icon-layout");
        var cache = new SvgIconCache { ContentRoot = tmp.Path };
        // A 2:1 icon (wide rectangle) to check the ratio is respected.
        tmp.Write("wide.svg", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 12"><rect x="0" y="0" width="24" height="12" fill="currentColor"/></svg>
            """);
        return (tmp, cache);
    }

    [Fact]
    public void IconWithoutDimensionsSizesTo16x16Default()
    {
        var (tmp, cache) = Fixture();
        try
        {
            var root = new ScreenPanel();
            root.SetInlineStyle("align-items", "flex-start");
            root.AddChild(new Icon { Name = "wide" });
            var engine = new YogaLayoutEngine();
            engine.Layout(root, 320, 200, null, null, cache);

            // The icon has an intrinsic 2:1 ratio, but the default is 16x16.
            Assert.Equal(16, root.Children[0].Layout.Width);
            Assert.Equal(16, root.Children[0].Layout.Height);
        }
        finally
        {
            cache.Clear();
            tmp.Dispose();
        }
    }

    [Fact]
    public void IconWithFixedWidthKeepsItsAspectRatio()
    {
        var (tmp, cache) = Fixture();
        try
        {
            var root = new ScreenPanel();
            root.SetInlineStyle("align-items", "flex-start");
            var icon = new Icon { Name = "wide" };
            icon.SetInlineStyle("width", "100px");
            root.AddChild(icon);
            var engine = new YogaLayoutEngine();
            engine.Layout(root, 320, 200, null, null, cache);

            // 2:1 ratio: width 100 -> height 50.
            Assert.Equal(100, icon.Layout.Width);
            Assert.Equal(50, icon.Layout.Height);
        }
        finally
        {
            cache.Clear();
            tmp.Dispose();
        }
    }
}
