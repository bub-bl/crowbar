using System.Numerics;
using Crowbar.Engine;
using Crowbar.Engine.Rendering2D;
using Xunit;

namespace Crowbar.Engine.Tests;

public class Renderer2DTextShadowTests
{
    [Fact]
    public void DrawText_WithShadow_EmitsShadowBeforeGlyphs()
    {
        var renderer = new Renderer2D();
        renderer.Begin(400, 200);
        var style = new TextStyle(24f, ColorF.White).WithShadow(new Vector2(2, 3), 4f, ColorF.FromRgba(0, 0, 0, 128));
        renderer.DrawText("Hi", new Vector2(10, 10), style);
        renderer.End();

        Assert.True(renderer.GlyphShadowCount > 0);
        Assert.Equal(0, renderer.GlyphShadowCount % 6);

        // The shadow quad carries the widened softness and the shadow color.
        var shadow = renderer.GlyphShadows[0];
        Assert.Equal(4f + 0.75f, shadow.Softness, 2);
        Assert.Equal(new Vector4(0, 0, 0, 128f / 255f), shadow.Color);

        // The shadow batch is recorded before the glyph batch.
        var shadowIndex = -1;
        var glyphIndex = -1;
        for (var i = 0; i < renderer.Commands.Count; i++)
        {
            if (renderer.Commands[i].Kind == BatchKind.GlyphShadow)
                shadowIndex = i;
            if (renderer.Commands[i].Kind == BatchKind.Glyph)
                glyphIndex = i;
        }
        Assert.True(shadowIndex >= 0);
        Assert.True(glyphIndex >= 0);
        Assert.True(shadowIndex < glyphIndex);
    }

    [Fact]
    public void DrawText_WithoutShadow_EmitsNoShadowQuads()
    {
        var renderer = new Renderer2D();
        renderer.Begin(400, 200);
        renderer.DrawText("Hi", new Vector2(10, 10), 24f, ColorF.White);
        renderer.End();

        Assert.Equal(0, renderer.GlyphShadowCount);
    }

    [Fact]
    public void GlyphShadowShader_ExposesExpectedBindings()
    {
        var shader = Shader.Load("Shaders/GlyphShadow.wgsl");
        Assert.Contains(shader.EntryPoints, e => e.Name == "vs_main");
        Assert.Contains(shader.EntryPoints, e => e.Name == "fs_main");
        Assert.Equal(3, shader.Bindings.Count);
        Assert.Contains(shader.Bindings, b => b.Slot == 0 && b.Kind == ShaderBindingKind.Texture);
        Assert.Contains(shader.Bindings, b => b.Slot == 1 && b.Kind == ShaderBindingKind.Sampler);
        Assert.Contains(shader.Bindings, b => b.Slot == 2 && b.Kind == ShaderBindingKind.UniformBuffer);
    }
}
