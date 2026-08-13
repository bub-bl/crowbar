using System.Numerics;
using Crowbar.Engine;
using Crowbar.Engine.Rendering2D;
using Xunit;

namespace Crowbar.Engine.Tests;

public class Renderer2DShadowTests
{
    [Fact]
    public void BoxShadow_ExpandsShapeBySpreadAndOffset()
    {
        var renderer = new Renderer2D();
        renderer.Begin(200, 200);
        renderer.DrawBoxShadow(new RectF(50, 50, 100, 80), 8, new Vector2(4, -6), 10, 2, ColorF.FromRgba(0, 0, 0, 128));
        renderer.End();

        var shadow = Assert.Single(renderer.Shadows);
        Assert.Equal(0f, shadow.Flags.X); // outer
        Assert.Equal(new Vector4(50 + 4 - 2, 50 - 6 - 2, 100 + 4, 80 + 4), shadow.Shape);
        Assert.Equal(new Vector4(50, 50, 100, 80), shadow.Box);
        Assert.Equal(10f, shadow.Radii.X); // shape radius = 8 + 2
        Assert.Equal(8f, shadow.Radii.Y);  // box radius
        Assert.Equal(10f, shadow.Radii.Z); // blur
        Assert.Equal(new Vector4(0, 0, 0, 128f / 255f), shadow.Color);
    }

    [Fact]
    public void InnerShadow_ErodesShapeBySpread()
    {
        var renderer = new Renderer2D();
        renderer.Begin(200, 200);
        renderer.DrawInnerShadow(new RectF(0, 0, 100, 80), 8, new Vector2(3, 3), 6, 4, ColorF.Black);
        renderer.End();

        var shadow = Assert.Single(renderer.Shadows);
        Assert.Equal(1f, shadow.Flags.X); // inner
        Assert.Equal(new Vector4(7, 7, 92, 72), shadow.Shape);
        Assert.Equal(4f, shadow.Radii.X); // shape radius = 8 - 4
        Assert.Equal(6f, shadow.Radii.Z); // blur
    }

    [Fact]
    public void Shadows_BatchIntoOneDraw()
    {
        var renderer = new Renderer2D();
        renderer.Begin(200, 200);
        renderer.DrawBoxShadow(new RectF(0, 0, 10, 10), 2, default, 4, 0, ColorF.Black);
        renderer.DrawBoxShadow(new RectF(20, 20, 10, 10), 2, default, 4, 0, ColorF.Black);
        renderer.End();

        Assert.Equal(2, renderer.ShadowCount);
        var command = Assert.Single(renderer.Commands);
        Assert.Equal(BatchKind.Shadow, command.Kind);
        Assert.Equal(2, command.Count);
    }

    [Fact]
    public void Shadow_BakesTransform()
    {
        var renderer = new Renderer2D();
        renderer.Begin(200, 200);
        renderer.PushTranslate(new Vector2(10, 20));
        renderer.DrawBoxShadow(new RectF(0, 0, 10, 10), 2, default, 4, 0, ColorF.Black);
        renderer.End();

        var shadow = Assert.Single(renderer.Shadows);
        Assert.Equal(10f, shadow.M0.Z);
        Assert.Equal(20f, shadow.M1.Z);
    }

    [Fact]
    public void Shadow_CapturesClipStack()
    {
        var renderer = new Renderer2D();
        renderer.Begin(200, 200);
        renderer.PushClip(new RectF(0, 0, 50, 50), 2f);
        renderer.DrawBoxShadow(new RectF(0, 0, 10, 10), 2, default, 4, 0, ColorF.Black);
        renderer.End();

        var shadow = Assert.Single(renderer.Shadows);
        Assert.Equal(1f, shadow.Flags.Y); // one clip
        Assert.Equal(new Vector4(0, 0, 50, 50), shadow.Clip0);
        Assert.Equal(2f, shadow.ClipRadii.X);
    }

    [Fact]
    public void ShadowShader_ExposesExpectedBindings()
    {
        var shader = Shader.Load("Shaders/Shadow.wgsl");
        Assert.Contains(shader.EntryPoints, e => e.Name == "vs_main");
        Assert.Contains(shader.EntryPoints, e => e.Name == "fs_main");
        Assert.Equal(2, shader.Bindings.Count);
        Assert.Contains(shader.Bindings, b => b.Slot == 0 && b.Kind == ShaderBindingKind.ReadOnlyStorageBuffer);
        Assert.Contains(shader.Bindings, b => b.Slot == 1 && b.Kind == ShaderBindingKind.UniformBuffer);
    }
}
