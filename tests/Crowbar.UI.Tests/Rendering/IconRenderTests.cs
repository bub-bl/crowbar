using Crowbar.UI;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// The <c>&lt;icon&gt;</c> asset pipeline resolves SVG files from
/// <c>Assets/Icons</c>; the intrinsic size (the SVG's own coordinate space)
/// feeds the layout engine's aspect-ratio sizing, and the GPU renderer draws
/// the parsed vector paths (see <c>UiTreePainterTests</c>). These tests verify
/// the Skia-free cache: viewBox resolution, content-root invalidation and path
/// traversal safety.
/// </summary>
public class IconRenderTests : IDisposable
{
    private readonly TempDirectory _tmp = TestUi.TempDir("icons");

    public IconRenderTests()
    {
        // A Crowbar-style icon with an explicit viewBox.
        _tmp.Write("square.svg", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect x="2" y="2" width="20" height="20" fill="currentColor"/></svg>
            """);
        // A Solar-style icon (subfolder path).
        Directory.CreateDirectory(Path.Combine(_tmp.Path, "Solar", "ui", "Bold"));
        _tmp.Write("Solar/ui/Bold/cursor.svg", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M4 3l16 8-6 2-2 6z" fill="#1C274C"/></svg>
            """);
    }

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void IntrinsicSizeComesFromTheViewBox()
    {
        var cache = new SvgIconCache { ContentRoot = _tmp.Path };
        Assert.True(cache.TryGetIntrinsicSize("square", out var width, out var height));
        Assert.Equal(24f, width);
        Assert.Equal(24f, height);

        // Subfolder paths are allowed.
        Assert.True(cache.TryGetIntrinsicSize("Solar/ui/Bold/cursor", out var sw, out var sh));
        Assert.Equal(24f, sw);
        Assert.Equal(24f, sh);
        cache.Clear();
    }

    [Fact]
    public void ChangingContentRootInvalidatesResolvedAssets()
    {
        using var secondRoot = TestUi.TempDir("icons-second");
        secondRoot.Write("square.svg", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 24"><circle cx="12" cy="12" r="10" fill="currentColor"/></svg>
            """);

        var cache = new SvgIconCache { ContentRoot = _tmp.Path };
        Assert.True(cache.TryGetIntrinsicSize("square", out var w, out _));
        Assert.Equal(24f, w);

        cache.ContentRoot = secondRoot.Path;
        Assert.True(cache.TryGetIntrinsicSize("square", out var w2, out var h2));
        Assert.Equal(48f, w2);
        Assert.Equal(24f, h2);
        cache.Clear();
    }

    [Fact]
    public void TraversalNamesAreRejected()
    {
        var cache = new SvgIconCache { ContentRoot = _tmp.Path };
        Assert.False(cache.TryGetIntrinsicSize("../../secret", out _, out _));
        Assert.False(cache.TryGetIntrinsicSize("/absolute", out _, out _));
        cache.Clear();
    }

    [Fact]
    public void MissingIconReportsNoSize()
    {
        var cache = new SvgIconCache { ContentRoot = _tmp.Path };
        Assert.False(cache.TryGetIntrinsicSize("does-not-exist", out _, out _));
        cache.Clear();
    }
}
