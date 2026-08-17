using System.Numerics;
using Crowbar.Engine;
using Crowbar.Engine.Rendering2D;
using Xunit;

namespace Crowbar.Engine.Tests;

/// <summary>
/// Headless tests of the image path: atlas packing/UV computation and the
/// <see cref="Renderer2D.DrawImage"/> quad emission with object-fit.
/// </summary>
public class Renderer2DImageTests
{
    private static Texture2D MakeTexture(int width, int height) =>
        Texture2D.Create("test", width, height, new byte[checked(width * height * 4)]);

    private static Image2D MakeImage(int width, int height) => new()
    {
        Source = MakeTexture(width, height),
        UvRect = new RectF(0, 0, 1, 1)
    };

    [Fact]
    public void Atlas_PacksImagesWithoutOverlap()
    {
        var atlas = new TextureAtlas(null);
        var a = atlas.Add(MakeTexture(10, 10));
        var b = atlas.Add(MakeTexture(10, 10));

        Assert.NotEqual((a.AtlasX, a.AtlasY), (b.AtlasX, b.AtlasY));
        // The UV rects are disjoint and inside the unit square.
        Assert.True(a.UvRect.Right <= b.UvRect.X || b.UvRect.Right <= a.UvRect.X ||
                    a.UvRect.Bottom <= b.UvRect.Y || b.UvRect.Bottom <= a.UvRect.Y);
        Assert.InRange(a.UvRect.X, 0f, 1f);
        Assert.InRange(a.UvRect.Right, 0f, 1f);
        Assert.InRange(b.UvRect.X, 0f, 1f);
        Assert.InRange(b.UvRect.Right, 0f, 1f);
    }

    [Fact]
    public void Atlas_GrowsForOversizedImage()
    {
        var atlas = new TextureAtlas(null);
        var image = atlas.Add(MakeTexture(300, 300));

        Assert.True(atlas.Size >= 300);
        Assert.InRange(image.UvRect.Right, 0f, 1f);
        Assert.InRange(image.UvRect.Bottom, 0f, 1f);
    }

    [Fact]
    public void Atlas_GrowsAndRepacks_KeepingExistingUvRects()
    {
        var atlas = new TextureAtlas(null);
        var first = atlas.Add(MakeTexture(200, 200));
        var firstUv = first.UvRect;
        // Filling the initial 256x256 atlas forces a grow + repack.
        atlas.Add(MakeTexture(200, 200));
        atlas.Add(MakeTexture(200, 200));

        Assert.True(atlas.Size >= 256);
        // The first image still has a valid, in-bounds UV rect after repacking.
        Assert.InRange(first.UvRect.X, 0f, 1f);
        Assert.InRange(first.UvRect.Right, 0f, 1f);
        Assert.NotEqual(firstUv, first.UvRect);
    }

    [Fact]
    public void DrawImage_Stretch_EmitsFullDestQuad()
    {
        var renderer = new Renderer2D();
        renderer.Begin(200, 200);
        renderer.DrawImage(new RectF(10, 20, 100, 50), MakeImage(100, 50), ColorF.White);
        renderer.End();

        Assert.Equal(6, renderer.TexturedCount);
        var verts = renderer.TexturedVerts;
        // First triangle: TL(10,20) TR(110,20) BR(110,70).
        Assert.Equal(new Vector2(10, 20), verts[0].Position);
        Assert.Equal(new Vector2(110, 20), verts[1].Position);
        Assert.Equal(new Vector2(110, 70), verts[2].Position);
        Assert.Equal(new Vector2(0, 0), verts[0].Uv);
        Assert.Equal(new Vector2(1, 0), verts[1].Uv);
        Assert.Equal(new Vector2(1, 1), verts[2].Uv);

        var command = Assert.Single(renderer.Commands);
        Assert.Equal(BatchKind.Textured, command.Kind);
        Assert.Equal(6, command.Count);
    }

    [Fact]
    public void DrawImage_Contain_CentersAndPreservesAspect()
    {
        var renderer = new Renderer2D();
        renderer.Begin(200, 200);
        // 100x50 image into a 100x100 box: fitted 100x50, centered vertically (y+25).
        renderer.DrawImage(new RectF(0, 0, 100, 100), MakeImage(100, 50), ColorF.White, ImageFit.Contain);
        renderer.End();

        var verts = renderer.TexturedVerts;
        Assert.Equal(new Vector2(0, 25), verts[0].Position);
        Assert.Equal(new Vector2(100, 25), verts[1].Position);
        Assert.Equal(new Vector2(100, 75), verts[2].Position);
    }

    [Fact]
    public void DrawImage_Cover_ScalesUpAndCenters()
    {
        var renderer = new Renderer2D();
        renderer.Begin(300, 300);
        // 50x50 image into a 100x200 box: cover scale = max(2, 4) = 4 → 200x200,
        // centered horizontally at x = (100 - 200) / 2 = -50.
        renderer.DrawImage(new RectF(0, 0, 100, 200), MakeImage(50, 50), ColorF.White, ImageFit.Cover);
        renderer.End();

        var verts = renderer.TexturedVerts;
        Assert.Equal(new Vector2(-50, 0), verts[0].Position);
        Assert.Equal(new Vector2(150, 0), verts[1].Position);
        Assert.Equal(new Vector2(150, 200), verts[2].Position);
    }

    [Fact]
    public void DrawImage_Center_DrawsAtIntrinsicSize()
    {
        var renderer = new Renderer2D();
        renderer.Begin(200, 200);
        // 40x20 image into a 100x100 box: centered at (30, 40).
        renderer.DrawImage(new RectF(0, 0, 100, 100), MakeImage(40, 20), ColorF.White, ImageFit.Center);
        renderer.End();

        var verts = renderer.TexturedVerts;
        Assert.Equal(new Vector2(30, 40), verts[0].Position);
        Assert.Equal(new Vector2(70, 40), verts[1].Position);
        Assert.Equal(new Vector2(70, 60), verts[2].Position);
    }

    [Fact]
    public void DrawImage_Tint_BakesColor()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawImage(new RectF(0, 0, 50, 50), MakeImage(10, 10), ColorF.FromRgba(255, 0, 0, 128));
        renderer.End();

        Assert.All(renderer.TexturedVerts, v => Assert.Equal(new Vector4(1, 0, 0, 128f / 255f), v.Color));
    }

    [Fact]
    public void DrawImage_UnderTransform_TransformsQuad()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.PushTranslate(new Vector2(10, 0));
        renderer.DrawImage(new RectF(0, 0, 50, 50), MakeImage(10, 10), ColorF.White);
        renderer.End();

        Assert.Equal(new Vector2(10, 0), renderer.TexturedVerts[0].Position);
    }

    [Fact]
    public void DrawImage_InterleavesWithShapes_OrderedBatches()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawRect(new RectF(0, 0, 10, 10), ColorF.White);
        renderer.DrawImage(new RectF(0, 0, 50, 50), MakeImage(10, 10), ColorF.White);
        renderer.DrawRect(new RectF(5, 5, 10, 10), ColorF.White);
        renderer.End();

        Assert.Equal(3, renderer.Commands.Count);
        Assert.Equal(BatchKind.Sdf, renderer.Commands[0].Kind);
        Assert.Equal(BatchKind.Textured, renderer.Commands[1].Kind);
        Assert.Equal(BatchKind.Sdf, renderer.Commands[2].Kind);
    }

    [Fact]
    public void DrawImage_DisposedImage_IsSkipped()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        var image = MakeImage(10, 10);
        image.Dispose();
        renderer.DrawImage(new RectF(0, 0, 50, 50), image, ColorF.White);
        renderer.End();

        Assert.Equal(0, renderer.TexturedCount);
    }

    [Fact]
    public void TexturedShader_ExposesExpectedEntryPointsAndBindings()
    {
        var shader = Shader.Load("Shaders/Ui/Image.wgsl");
        Assert.Contains(shader.EntryPoints, e => e.Name == "vs_main");
        Assert.Contains(shader.EntryPoints, e => e.Name == "fs_main");
        Assert.Equal(3, shader.Bindings.Count);
        Assert.Contains(shader.Bindings, b => b.Slot == 0 && b.Kind == ShaderBindingKind.Texture);
        Assert.Contains(shader.Bindings, b => b.Slot == 1 && b.Kind == ShaderBindingKind.Sampler);
        Assert.Contains(shader.Bindings, b => b.Slot == 2 && b.Kind == ShaderBindingKind.UniformBuffer);
    }
}
