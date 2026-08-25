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
        ui.Prepare(); // run layout + cascade
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

    [Fact]
    public void Paint_LayeredOverlayEscapesAncestorOverflowClip()
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
            .box       { position: absolute; left: 5px; top: 5px; width: 200px; height: 30px; background-color: #00ff00; z-index: 10; }
            """);

        var (renderer, _) = Paint(ui);

        // Despite living inside an overflow:hidden box, the layering overlay
        // (absolute + z-index) is deferred and painted above the clip: no clip
        // is applied to it.
        var boxInstance = Assert.Single(renderer.Instances, i => i.Color == new Vector4(0, 1, 0, 1));
        Assert.Equal(0f, boxInstance.Flags.Z);   // no active clip
        Assert.Equal(Vector4.Zero, boxInstance.Clip0);
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

    [Fact]
    public void Paint_TextEllipsisTruncatesOverflowingNowrapText()
    {
        using var ui = TestUi.Create(320, 200);
        const string full = "Directional Light Directional Light";
        var label = new Label(full);
        label.AddClass("label");
        ui.Screen.AddChild(label);
        ui.LoadStyles(".label { position: absolute; left: 10px; top: 10px; width: 70px; color: #ffffff; font-size: 20px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }");

        var (renderer, _) = Paint(ui);

        // Each glyph emits a 6-vertex quad, so TexturedCount / 6 is the glyph
        // count. The full string would draw one glyph per character; the
        // ellipsized run must draw fewer (the layout box was measured with a
        // trailing ellipsis, and the painter must draw the same truncation).
        var glyphCount = renderer.TexturedCount / 6;
        Assert.True(glyphCount < full.Length, $"expected ellipsized text, drew {glyphCount} glyphs");
        Assert.True(glyphCount > 1, "expected at least the ellipsis character");
    }

    [Fact]
    public void Paint_CenteredEllipsizedTextKeepsLeadingGlyphInsideBox()
    {
        using var ui = TestUi.Create(320, 200);
        var label = new Label("Crate_basecolor_roughness");
        label.AddClass("label");
        ui.Screen.AddChild(label);
        ui.LoadStyles(".label { position: absolute; left: 10px; top: 10px; width: 70px; color: #ffffff; font-size: 20px; text-align: center; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }");

        var (renderer, _) = Paint(ui);

        Assert.NotEmpty(renderer.TexturedVerts);
        var minX = renderer.TexturedVerts.Min(vertex => vertex.Position.X);
        Assert.True(minX >= 10f, $"the leading glyph must not be clipped by the text container (min X: {minX:0.##})");
    }

    [Fact]
    public void Paint_TextEllipsisInheritedByDescendantTextNode()
    {
        // The real editor tree is <span class="tree-text">Label</span>: the
        // text lives on a descendant text node, not on the element that
        // declares text-overflow. The keyword must inherit so the truncation
        // reaches the glyphs it targets.
        using var ui = TestUi.Create(320, 200);
        const string full = "Directional Light Directional Light";
        var span = new Panel { TagName = "span" };
        span.AddClass("tree-text");
        var textNode = new Panel { TagName = "text", Text = full };
        span.AddChild(textNode);
        ui.Screen.AddChild(span);
        ui.LoadStyles(".tree-text { position: absolute; left: 10px; top: 10px; width: 70px; color: #ffffff; font-size: 20px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }");

        Paint(ui);

        // text-overflow flows down to the descendant text node (the panel that
        // actually carries the glyphs), unlike a hard clip that only culls them.
        Assert.Equal("ellipsis", textNode.ComputedStyle.TextOverflow);
    }

    [Fact]
    public void Paint_OverflowHiddenClipsDescendantTextGlyphs()
    {
        using var ui = TestUi.Create(320, 200);
        const string full = "Directional Light Directional Light";
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        box.AddChild(new Panel { TagName = "text", Text = full });
        ui.Screen.AddChild(box);
        ui.LoadStyles(".box { position: absolute; left: 10px; top: 10px; width: 70px; height: 30px; color: #ffffff; font-size: 20px; overflow: hidden; }");

        var (renderer, _) = Paint(ui);

        // The full string is far wider than the 70px box, so every emitted glyph
        // quad must stay inside the clip rect (the box's content box) instead of
        // spilling past its right edge.
        Assert.NotEmpty(renderer.TexturedVerts);
        Assert.All(renderer.TexturedVerts, v =>
        {
            Assert.InRange(v.Position.X, 9f, 81f);
            Assert.InRange(v.Position.Y, 9f, 41f);
        });
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

        ui.Prepare();
        var renderer = new Renderer2D();
        var painter = new UiTreePainter(renderer);
        var texture = Texture2D.Create("test", 2, 2, new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 });
        painter.ImageResolver = _ => renderer.RegisterImage(texture);
        painter.Paint(ui.Screen);

        Assert.True(renderer.TexturedCount > 0);
        Assert.Equal(0, renderer.TexturedCount % 6);
        Assert.Contains(renderer.Commands, c => c.Kind == BatchKind.Textured);
    }

    [Fact]
    public void Paint_BackgroundImageTilesWithRepeat()
    {
        using var ui = TestUi.Create(160, 160);
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        ui.Screen.AddChild(box);
        ui.LoadStyles(".box { position: absolute; left: 0px; top: 0px; width: 128px; height: 64px; background-image: url(tile.png); background-size: 32px 32px; background-repeat: repeat; }");

        ui.Prepare();
        var renderer = new Renderer2D();
        var painter = new UiTreePainter(renderer);
        var texture = Texture2D.Create("tile", 2, 2, new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 });
        painter.ImageResolver = _ => renderer.RegisterImage(texture);
        painter.Paint(ui.Screen);

        // 128x64 area tiled with 32x32 tiles = 4 columns x 2 rows = 8 quads.
        Assert.Equal(8 * 6, renderer.TexturedCount);
    }

    [Fact]
    public void Paint_BackgroundImageNoRepeatHonorsPosition()
    {
        using var ui = TestUi.Create(200, 120);
        var box = new Panel { TagName = "div" };
        box.AddClass("box");
        ui.Screen.AddChild(box);
        ui.LoadStyles(".box { position: absolute; left: 0px; top: 0px; width: 120px; height: 80px; background-image: url(tile.png); background-size: 40px 40px; background-repeat: no-repeat; background-position: 50% 50%; }");

        ui.Prepare();
        var renderer = new Renderer2D();
        var painter = new UiTreePainter(renderer);
        var texture = Texture2D.Create("tile", 2, 2, new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 });
        painter.ImageResolver = _ => renderer.RegisterImage(texture);
        painter.Paint(ui.Screen);

        // A single centered tile: (120-40)/2 = 40 offset on each axis.
        Assert.Equal(6, renderer.TexturedCount);
        var quad = renderer.TexturedVerts[0];
        Assert.Equal(40f, quad.Position.X, 2);
        Assert.Equal(20f, quad.Position.Y, 2); // (80-40)/2
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

    // --- Pseudo elements and caret ---------------------------------------

    [Fact]
    public void Paint_PseudoElementEmitsText()
    {
        using var ui = TestUi.Create(200, 100);
        var badge = new Panel { TagName = "div" };
        badge.AddClass("badge");
        ui.Screen.AddChild(badge);
        ui.LoadStyles(".badge { position: absolute; left: 10px; top: 10px; width: 50px; height: 20px; } .badge::after { content: \" ✓\"; color: #00aa00; }");
        ui.Prepare();
        Assert.NotNull(badge.PseudoAfter);

        var renderer = new Renderer2D();
        var painter = new UiTreePainter(renderer);
        painter.Paint(ui.Screen);

        Assert.True(renderer.TexturedCount > 0);
        Assert.Contains(renderer.Commands, c => c.Kind == BatchKind.Glyph);
    }

    [Fact]
    public void Paint_EmptyInputDrawsPlaceholder()
    {
        using var ui = TestUi.Create(240, 100);
        ui.LoadRazor("""
            <input class="field" placeholder="Search..." />
            """, "PlaceholderDemo");
        ui.LoadStyles(".field { position: absolute; left: 10px; top: 10px; width: 160px; height: 24px; color: #ffffff; font-size: 16px; white-space: nowrap; }");
        ui.Prepare();

        var input = TestUi.Find(ui.Screen, p => p is TextInput) as TextInput;
        Assert.NotNull(input);
        Assert.Equal("Search...", input!.Placeholder);

        var renderer = new Renderer2D();
        new UiTreePainter(renderer).Paint(ui.Screen);

        Assert.Contains(renderer.Commands, c => c.Kind == BatchKind.Glyph);
    }

    [Fact]
    public void Paint_FocusedInputDrawsCaret()
    {
        using var ui = TestUi.Create(240, 100);
        var input = new TextInput();
        input.AddClass("field");
        ui.Screen.AddChild(input);
        ui.LoadStyles(".field { position: absolute; left: 10px; top: 10px; width: 160px; height: 24px; color: #ffffff; font-size: 16px; white-space: nowrap; }");
        ui.Prepare();
        input.SetValue("hello");
        input.SetFocused(true);
        input.FocusAtEnd();
        ui.Prepare();

        var renderer = new Renderer2D();
        var painter = new UiTreePainter(renderer);
        painter.Paint(ui.Screen);

        // The caret is a line shape with a stroke width.
        var caret = Assert.Single(renderer.Instances, i => i.Flags.X == (float)ShapeKind.Line);
        Assert.True(caret.Params.Y > 0f);
    }

    // --- Backdrops --------------------------------------------------------

    [Fact]
    public void Paint_CollectsBackdropRegions()
    {
        using var ui = TestUi.Create(200, 120);
        var glass = new Panel { TagName = "div" };
        glass.AddClass("glass");
        ui.Screen.AddChild(glass);
        ui.LoadStyles(".glass { position: absolute; left: 10px; top: 10px; width: 120px; height: 60px; background-color: #ffffff33; backdrop-filter: blur(6px); }");
        ui.Prepare();

        var renderer = new Renderer2D();
        var painter = new UiTreePainter(renderer);
        painter.Paint(ui.Screen);

        var region = Assert.Single(painter.Backdrops);
        Assert.Equal(10f, region.X);
        Assert.Equal(10f, region.Y);
        Assert.Equal(120f, region.Width);
        Assert.Equal(60f, region.Height);
    }

    // --- Tooltip ----------------------------------------------------------

    [Fact]
    public void Paint_DrawsTooltipOnTopOfTheTree()
    {
        using var ui = TestUi.Create(200, 120);
        var (renderer, painter) = Paint(ui, p => p.SetTooltip("hover", new Vector2(20, 20)));

        // The tooltip paints a filled box + a border ring, plus glyphs for the text.
        Assert.True(renderer.Instances.Count >= 2, $"expected tooltip box + border, got {renderer.Instances.Count} instances");
        Assert.Contains(renderer.Commands, c => c.Kind == BatchKind.Glyph);
    }

    [Fact]
    public void Paint_NoTooltipEmitsNoOverlay()
    {
        using var ui = TestUi.Create(200, 120);
        var (renderer, _) = Paint(ui);

        Assert.Empty(renderer.Instances);
        Assert.Empty(renderer.Commands);
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
