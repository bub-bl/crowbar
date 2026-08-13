using Crowbar.Engine.Rendering2D;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// The viewport gizmo icons (Assets/Gizmos/*.svg) are rasterized once at
/// startup by the engine's own <see cref="SvgRasterizer"/> (no Skia). These
/// tests exercise the real assets — including <c>fill-rule="evenodd"</c>,
/// <c>opacity</c> and cubic/quadratic/arc path data — so a regression in the
/// parser or rasterizer fails here instead of crashing editor startup.
/// </summary>
public class GizmoIconRasterizationTests
{
    [Theory]
    [InlineData("camera.svg")]
    [InlineData("directional-light.svg")]
    [InlineData("mesh.svg")]
    [InlineData("point-light.svg")]
    public void GizmoSvgRasterizesToNonEmptyIcon(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Gizmos", fileName);
        Assert.True(File.Exists(path), $"gizmo asset missing: {path}");

        var shape = SvgDocumentParser.Parse(File.ReadAllText(path));
        var pixels = SvgRasterizer.Rasterize(shape, 32, ColorF.White);

        Assert.Equal(32 * 32 * 4, pixels.Length);

        // Every icon covers a meaningful fraction of its 32x32 cell.
        var covered = 0;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] > 0)
                covered++;
        Assert.True(covered > 20, $"{fileName} rasterized to only {covered} covered pixels");
    }
}
