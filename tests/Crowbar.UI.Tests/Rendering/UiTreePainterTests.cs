using System.Numerics;
using Crowbar.Engine;
using Crowbar.Engine.Rendering2D;
using Crowbar.UI;
using Xunit;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>
/// Headless tests of the tree-walk painter (<see cref="UiTreePainter"/>): it
/// records a laid-out panel tree into a <see cref="Renderer2D"/> without a GPU.
/// These verify the CSS paint order, clip/transform/filter wiring, and the
/// text/image/SVG/shadow emission — the migration path off the Skia rasterizer.
/// </summary>
public class UiTreePainterTests
{
    private static (Renderer2D Renderer, UiTreePainter Painter) Paint(UiSystem ui, Action<UiTreePainter>? configure = null)
    {
        ui.Render(); // run layout + cascade
        var renderer = new Renderer2D();
        var painter = new UiTreePainter(renderer);
        configure?.Invoke(painter);
        painter.Paint(ui.Screen);
        return (renderer, painter);
    }

    // --- Backgrounds and paint order --------------------------------------

    [Fact]
    public void Paint_DrawsBackgroundAsRoundedRect()
    {
        using var ui = TestUi.Create(160, 160);
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        ui.Screen.AddChild(box);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; border-radius: 6px; }");

        var (renderer, _) = Paint(ui);

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal((float)ShapeKind.RoundedRect, instance.Flags.X);
        Assert.Equal(new Vector4(0, 1, 0, 1), instance.Color);
        // Border radius travels in Params.X.
        Assert.Equal(6f, instance.Params.X, 3);
    }

    [Fact]
    public void Paint_PreservesDocumentOrderForSiblings()
    {
        using var ui = TestUi.Create(160, 160);
        var a = new Panel { TagName = "div" };
        a.AddClass("a");
        var b = new Panel { TagName = "div" };
        b.AddClass("b");
        ui.Screen.AddChild(a);
        ui.Screen.AddChild(b);
        ui.LoadStyles("""
            .a { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #ff0000; }
            .b { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #0000ff; }
            """);

        var (renderer, _) = Paint(ui);

        Assert.Equal(2, renderer.InstanceCount);
        Assert.Equal(new Vector4(1, 0, 0, 1), renderer.Instances[0].Color);
        Assert.Equal(new Vector4(0, 0, 1, 1), renderer.Instances[1].Color);
    }

    [Fact]
    public void Paint_ZIndexOrdersSiblingsAscending()
    {
        using var ui = TestUi.Create(160, 160);
        var a = new Panel { TagName = "div" };
        a.AddClass("a");
        var b = new Panel { TagName = "div" };
        b.AddClass("b");
        ui.Screen.AddChild(a);
        ui.Screen.AddChild(b);
        ui.LoadStyles("""
            .a { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #ff0000; z-index: 5; }
            .b { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #0000ff; z-index: 1; }
            """);

        var (renderer, _) = Paint(ui);

        Assert.Equal(2, renderer.InstanceCount);
        // Lower z-index (b) paints first, higher (a) paints on top.
        Assert.Equal(new Vector4(0, 0, 1, 1), renderer.Instances[0].Color);
        Assert.Equal(new Vector4(1, 0, 0, 1), renderer.Instances[1].Color);
    }

    // --- Clipping ---------------------------------------------------------

    [Fact]
    public void Paint_OverflowHiddenClipsChildren()
    {
        using var ui = TestUi.Create(160, 160);
        var container = new Panel { TagName = "div" };
        container.AddClass("container");
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        container.AddChild(box);
        ui.Screen.AddChild(container);
        ui.LoadStyles("""
            .container { position: absolute; left: 10px; top: 10px; width: 120px; height: 60px; overflow: hidden; }
            .box       { position: absolute; left: 5px; top: 5px; width: 200px; height: 30px; background-color: #00ff00; }
            """);

        var (renderer, _) = Paint(ui);

        var boxInstance = Assert.Single(renderer.Instances, i => i.Color == new Vector4(0, 1, 0, 1));
        Assert.Equal(1f, boxInstance.Flags.Z);       // one active clip
        Assert.NotEqual(Vector4.Zero, boxInstance.Clip0);
    }

    // --- Transform --------------------------------------------------------

    [Fact]
    public void Paint_TranslateMovesGeometry()
    {
        using var ui = TestUi.Create(160, 160);
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        ui.Screen.AddChild(box);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; transform: translate(50px, 30px); }");

        var (renderer, _) = Paint(ui);

        var instance = Assert.Single(renderer.Instances);
        // Local box (0,0,100,50) maps to screen (10+50, 10+30).
        Assert.Equal(60f, instance.M0.Z, 2);
        Assert.Equal(40f, instance.M1.Z, 2);
        Assert.Equal(new Vector4(0, 0, 100, 50), instance.Rect);
    }

    // --- Box shadows ------------------------------------------------------

    [Fact]
    public void Paint_OuterBoxShadowEmitsShadowInstance()
    {
        using var ui = TestUi.Create(160, 160);
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        ui.Screen.AddChild(box);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; box-shadow: 4px 4px 6px 2px #00000080; }");

        var (renderer, _) = Paint(ui);

        var shadow = Assert.Single(renderer.Shadows);
        Assert.Equal(0f, shadow.Flags.X); // outer
        Assert.Equal(6f, shadow.Radii.Z, 3); // blur radius
    }

    [Fact]
    public void Paint_InsetBoxShadowEmitsShadowInstance()
    {
        using var ui = TestUi.Create(160, 160);
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        ui.Screen.AddChild(box);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; box-shadow: inset 2px 2px 4px #00000080; }");

        var (renderer, _) = Paint(ui);

        var shadow = Assert.Single(renderer.Shadows);
        Assert.Equal(1f, shadow.Flags.X); // inner
    }

    // --- Text -------------------------------------------------------------

    [Fact]
    public void Paint_TextEmitsGlyphQuads()
    {
        using var ui = TestUi.Create(320, 200);
        var label = new Label("Hello");
        label.AddClass("label");
        ui.Screen.AddChild(label);
        ui.LoadStyles(".label { position: absolute; left: 10px; top: 10px; color: #ffffff; font-size: 20px; }");

        var (renderer, _) = Paint(ui);

        Assert.True(renderer.TexturedCount > 0);
        Assert.Equal(0, renderer.TexturedCount % 6);
        Assert.Contains(renderer.Commands, c => c.Kind == BatchKind.Glyph);
    }

    [Fact]
    public void Paint_TextBakesColorIntoQuads()
    {
        using var ui = TestUi.Create(320, 200);
        var label = new Label("A");
        label.AddClass("label");
        ui.Screen.AddChild(label);
        ui.LoadStyles(".label { position: absolute; left: 10px; top: 10px; color: #ff0000; font-size: 20px; }");

        var (renderer, _) = Paint(ui);

        Assert.NotEmpty(renderer.TexturedVerts);
        Assert.All(renderer.TexturedVerts, v => Assert.Equal(new Vector4(1, 0, 0, 1), v.Color));
    }

    [Fact]
    public void Paint_TextShadowEmitsGlyphShadows()
    {
        using var ui = TestUi.Create(320, 200);
        var label = new Label("Shadowed");
        label.AddClass("label");
        ui.Screen.AddChild(label);
        ui.LoadStyles(".label { position: absolute; left: 10px; top: 10px; color: #ffffff; font-size: 20px; text-shadow: 2px 2px 4px #000000; }");

        var (renderer, _) = Paint(ui);

        Assert.True(renderer.GlyphShadowCount > 0);
        Assert.Contains(renderer.Commands, c => c.Kind == BatchKind.GlyphShadow);
    }

    // --- Icons (SVG) ------------------------------------------------------

    [Fact]
    public void Paint_IconEmitsSvgTriangles()
    {
        using var ui = TestUi.Create(160, 160);
        var icon = new Icon { Name = "test" };
        icon.AddClass("icon");
        ui.Screen.AddChild(icon);
        ui.LoadStyles(".icon { position: absolute; left: 10px; top: 10px; width: 24px; height: 24px; color: #ff0000; }");

        var shape = SvgDocumentParser.Parse("<svg viewBox='0 0 24 24'><path d='M4 4h16v16H4z'/></svg>");
        var (renderer, _) = Paint(ui, p => p.IconResolver = _ => shape);

        Assert.True(renderer.TriangleCount > 0);
        Assert.Contains(renderer.Commands, c => c.Kind == BatchKind.Triangles);
        // The currentColor fill resolves to the computed tint.
        Assert.All(renderer.Triangles, t => Assert.Equal(new Vector4(1, 0, 0, 1), t.Color));
    }

    // --- Images -----------------------------------------------------------

    [Fact]
    public void Paint_ImageEmitsTexturedQuads()
    {
        using var ui = TestUi.Create(160, 160);
        var image = new Image { Source = "test.png" };
        image.AddClass("img");
        ui.Screen.AddChild(image);
        ui.LoadStyles(".img { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; }");

        ui.Render();
        var renderer = new Renderer2D();
        var painter = new UiTreePainter(renderer);
        var texture = Texture2D.Create("test", 2, 2, new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 });
        painter.ImageResolver = _ => renderer.RegisterImage(texture);
        painter.Paint(ui.Screen);

        Assert.True(renderer.TexturedCount > 0);
        Assert.Equal(0, renderer.TexturedCount % 6);
        Assert.Contains(renderer.Commands, c => c.Kind == BatchKind.Textured);
    }

    // --- Filters ----------------------------------------------------------

    [Fact]
    public void Paint_FilterCreatesCompositedLayer()
    {
        using var ui = TestUi.Create(160, 160);
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        ui.Screen.AddChild(box);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 100px; height: 50px; background-color: #00ff00; filter: blur(4px) brightness(1.2); }");

        var (renderer, _) = Paint(ui);

        Assert.Single(renderer.FilterLayers);
        Assert.Contains(renderer.Commands, c => c.Kind == BatchKind.FilterBlit);
        Assert.Equal(2, renderer.FilterLayers[0].Filter.Ops.Count);
    }

    // --- Text transform helper -------------------------------------------

    [Fact]
    public void ApplyTextTransform_HandlesAllModes()
    {
        Assert.Equal("HELLO", UiTreePainter.ApplyTextTransform("hello", "uppercase"));
        Assert.Equal("hello", UiTreePainter.ApplyTextTransform("HELLO", "lowercase"));
        Assert.Equal("Hello World", UiTreePainter.ApplyTextTransform("hello world", "capitalize"));
        Assert.Equal("Hello", UiTreePainter.ApplyTextTransform("Hello", "none"));
    }
}
