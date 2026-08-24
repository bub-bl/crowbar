using System.Numerics;
using Crowbar.Engine;
using Crowbar.Engine.Rendering2D;
using Xunit;

namespace Crowbar.Engine.Tests;

/// <summary>
/// CPU-side tests of <see cref="Renderer2D"/>'s recording, batching, transform
/// and clip model. These run headless (no <see cref="Rendering.IGraphicsDevice"/>),
/// so they verify the exact instance/vertex/command streams the WebGPU backend
/// will upload, without needing a GPU.
/// </summary>
public class Renderer2DTests
{
    private static Vector4 Color(ColorF c) => c.ToVector4();

    [Fact]
    public void BeginEnd_WithNoDraws_RecordsNothing()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        Assert.Null(renderer.End());
        Assert.Equal(0, renderer.InstanceCount);
        Assert.Equal(0, renderer.TriangleCount);
        Assert.Equal(0, renderer.BatchCount);
    }

    [Fact]
    public void DrawRect_EmitsSingleSdfInstance()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawRect(new RectF(10, 10, 30, 20), ColorF.White);
        renderer.End();

        Assert.Equal(1, renderer.InstanceCount);
        Assert.Equal(0, renderer.TriangleCount);
        var command = Assert.Single(renderer.Commands);
        Assert.Equal(BatchKind.Sdf, command.Kind);
        Assert.Equal((0, 1), (command.Start, command.Count));
    }

    [Fact]
    public void ConsecutiveShapes_BatchIntoOneDraw()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawRect(new RectF(0, 0, 10, 10), ColorF.White);
        renderer.DrawCircle(new Vector2(20, 20), 5, ColorF.White);
        renderer.DrawRoundedRect(new RectF(30, 30, 10, 10), 2, ColorF.White);
        renderer.DrawLine(new Vector2(0, 0), new Vector2(10, 10), 2, ColorF.White);
        renderer.End();

        Assert.Equal(4, renderer.InstanceCount);
        var command = Assert.Single(renderer.Commands);
        Assert.Equal(BatchKind.Sdf, command.Kind);
        Assert.Equal(4, command.Count);
    }

    [Fact]
    public void InterleavedPolygon_SplitsBatchesPreservingOrder()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawRect(new RectF(0, 0, 10, 10), ColorF.White);
        renderer.DrawPolygon(
            [new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10)], ColorF.White);
        renderer.DrawRect(new RectF(5, 5, 10, 10), ColorF.White);
        renderer.End();

        // Sdf(1) -> Triangles(6: two triangles for the square) -> Sdf(1).
        Assert.Equal(3, renderer.Commands.Count);
        Assert.Equal(BatchKind.Sdf, renderer.Commands[0].Kind);
        Assert.Equal(1, renderer.Commands[0].Count);
        Assert.Equal(BatchKind.Triangles, renderer.Commands[1].Kind);
        Assert.Equal(6, renderer.Commands[1].Count);
        Assert.Equal(BatchKind.Sdf, renderer.Commands[2].Kind);
        Assert.Equal(1, renderer.Commands[2].Count);
    }

    [Fact]
    public void Translate_BakesMatrixIntoInstance()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.PushTranslate(new Vector2(10, 20));
        renderer.DrawRect(new RectF(0, 0, 5, 5), ColorF.White);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal(10f, instance.M0.Z);   // screen.x translation
        Assert.Equal(20f, instance.M1.Z);   // screen.y translation
        Assert.Equal(1f, instance.M0.X);    // scale-x unchanged
        Assert.Equal(1f, instance.M1.Y);    // scale-y unchanged
    }

    [Fact]
    public void NestedTransforms_Compose()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.PushTranslate(new Vector2(10, 0));
        renderer.PushScale(2f);
        renderer.DrawRect(new RectF(0, 0, 5, 5), ColorF.White);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        // Canvas-style composition: translate(10,0) then scale(2) maps a point at
        // (5, 5) to (10 + 2*5, 2*5) = (20, 10) — the scale applies first.
        Assert.Equal(2f, instance.M0.X);   // scale-x
        Assert.Equal(2f, instance.M1.Y);   // scale-y
        Assert.Equal(10f, instance.M0.Z);  // translation unchanged
        var mapped = Vector2.Transform(new Vector2(5f, 5f), renderer.CurrentTransform);
        Assert.Equal(20f, mapped.X);
        Assert.Equal(10f, mapped.Y);
        renderer.PopTransform();
        renderer.PopTransform();
        Assert.Equal(Matrix3x2.Identity, renderer.CurrentTransform);
    }

    [Fact]
    public void PushClip_BakesScreenSpaceRect()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.PushTranslate(new Vector2(5, 5));
        renderer.PushClip(new RectF(0, 0, 10, 10), 2f);
        renderer.DrawRect(new RectF(0, 0, 50, 50), ColorF.White);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal(1f, instance.Flags.Z);                 // one active clip
        Assert.Equal(new Vector4(5, 5, 10, 10), instance.Clip0); // translated to screen space
        Assert.Equal(2f, instance.ClipRadii.X);             // radius survives (uniform scale)
    }

    [Fact]
    public void NestedClips_CaptureStackInOrder()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.PushClip(new RectF(0, 0, 100, 100));
        renderer.PushClip(new RectF(10, 10, 20, 20));
        renderer.DrawRect(new RectF(0, 0, 100, 100), ColorF.White);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal(2f, instance.Flags.Z);
        Assert.Equal(new Vector4(0, 0, 100, 100), instance.Clip0);
        Assert.Equal(new Vector4(10, 10, 20, 20), instance.Clip1);
    }

    [Fact]
    public void PopClip_RemovesClip()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.PushClip(new RectF(0, 0, 10, 10));
        renderer.PopClip();
        renderer.DrawRect(new RectF(0, 0, 100, 100), ColorF.White);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal(0f, instance.Flags.Z);
    }

    [Fact]
    public void LinearGradient_BakesKindAndSortedStops()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        var gradient = LinearGradient.Create(
            new Vector2(0, 0), new Vector2(100, 0),
            [new GradientStop(1f, ColorF.FromRgba(255, 0, 0)), new GradientStop(0f, ColorF.FromRgba(0, 0, 255))]);
        renderer.DrawRect(new RectF(0, 0, 100, 100), gradient);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal(1f, instance.Flags.Y);   // linear
        Assert.Equal(2f, instance.Flags.W);   // two stops
        // Offsets were normalized ascending: 0 then 1.
        Assert.Equal(0f, instance.Offsets.X);
        Assert.Equal(1f, instance.Offsets.Y);
        Assert.Equal(new Vector4(0, 0, 1, 1), instance.Stop0); // blue first
        Assert.Equal(new Vector4(1, 0, 0, 1), instance.Stop1); // then red
        Assert.Equal(new Vector4(0, 0, 0, 0), instance.Grad0); // start
        Assert.Equal(new Vector4(100, 0, 0, 0), instance.Grad1); // end
    }

    [Fact]
    public void RadialGradient_BakesCenterRadius()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        var gradient = RadialGradient.Create(
            new Vector2(50, 50), 25f,
            [new GradientStop(0f, ColorF.White), new GradientStop(1f, ColorF.Black)]);
        renderer.DrawCircle(new Vector2(50, 50), 25, gradient);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal(2f, instance.Flags.Y);    // radial
        Assert.Equal(new Vector4(50, 50, 0, 0), instance.Grad0); // center
        Assert.Equal(25f, instance.Grad1.X);   // radius
    }

    [Fact]
    public void Polygon_SquareTriangulatesIntoTwoTriangles()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawPolygon(
            [new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10)], ColorF.White);
        renderer.End();

        Assert.Equal(0, renderer.InstanceCount);
        Assert.Equal(6, renderer.TriangleCount); // 2 triangles
        var command = Assert.Single(renderer.Commands);
        Assert.Equal(BatchKind.Triangles, command.Kind);
    }

    [Fact]
    public void Polygon_OutsideClip_IsDiscarded()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.PushClip(new RectF(0, 0, 10, 10));
        // The polygon lies entirely outside the clip.
        renderer.DrawPolygon(
            [new Vector2(50, 50), new Vector2(60, 50), new Vector2(60, 60)], ColorF.White);
        renderer.End();

        Assert.Equal(0, renderer.TriangleCount);
    }

    [Fact]
    public void Polygon_PartiallyClipped_IsTrimmedToRect()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.PushClip(new RectF(2, 2, 4, 4));
        renderer.DrawPolygon(
            [new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10)], ColorF.White);
        renderer.End();

        // The clipped polygon is the 4x4 clip square: 2 triangles.
        Assert.Equal(6, renderer.TriangleCount);
    }

    [Fact]
    public void Stroke_SetsStrokeWidthParam()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawRoundedRect(new RectF(0, 0, 20, 20), 4, 2, ColorF.White);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal(4f, instance.Params.X);  // radius
        Assert.Equal(2f, instance.Params.Y);  // stroke width
    }

    [Fact]
    public void Line_StoresSegmentAndBoundingBox()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawLine(new Vector2(0, 0), new Vector2(10, 10), 2, ColorF.White);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal(3f, instance.Flags.X);                      // line kind
        Assert.Equal(new Vector4(0, 0, 0, 0), instance.Grad0);   // start
        Assert.Equal(new Vector4(10, 10, 0, 0), instance.Grad1); // end
        Assert.Equal(2f, instance.Params.Y);                     // width
        // Bounding box inflated by half the width.
        Assert.Equal(-1f, instance.Rect.X);
        Assert.Equal(12f, instance.Rect.Z);
    }

    [Fact]
    public void SecondFrame_DoesNotGrowBuffers()
    {
        var renderer = new Renderer2D();
        RenderFrame(renderer);
        var instanceCapacity = renderer.InstanceCapacity;
        var triangleCapacity = renderer.TriangleCapacity;
        var commandCapacity = renderer.CommandCapacity;

        RenderFrame(renderer);

        // A steady-state frame reuses every buffer at its existing capacity.
        Assert.Equal(instanceCapacity, renderer.InstanceCapacity);
        Assert.Equal(triangleCapacity, renderer.TriangleCapacity);
        Assert.Equal(commandCapacity, renderer.CommandCapacity);
    }

    [Fact]
    public void Shaders_ExposeExpectedEntryPointsAndBindings()
    {
        var sdf = Shader.Load("Shaders/Ui/Shape.wgsl");
        Assert.Contains(sdf.EntryPoints, e => e.Name == "vs_main");
        Assert.Contains(sdf.EntryPoints, e => e.Name == "fs_main");
        Assert.Equal(2, sdf.Bindings.Count);
        Assert.Contains(sdf.Bindings, b => b.Slot == 0 && b.Kind == ShaderBindingKind.ReadOnlyStorageBuffer);
        Assert.Contains(sdf.Bindings, b => b.Slot == 1 && b.Kind == ShaderBindingKind.UniformBuffer);

        var tri = Shader.Load("Shaders/Ui/Polygon.wgsl");
        Assert.Contains(tri.EntryPoints, e => e.Name == "vs_main");
        Assert.Contains(tri.EntryPoints, e => e.Name == "fs_main");
        var binding = Assert.Single(tri.Bindings);
        Assert.Equal((0u, ShaderBindingKind.UniformBuffer), (binding.Slot, binding.Kind));
    }

    private static void RenderFrame(Renderer2D renderer)
    {
        renderer.Begin(200, 200);
        renderer.PushClip(new RectF(0, 0, 200, 200));
        renderer.DrawRect(new RectF(0, 0, 100, 100), ColorF.FromRgba(255, 0, 0, 128));
        renderer.DrawCircle(new Vector2(50, 50), 10, ColorF.White);
        renderer.DrawLine(new Vector2(0, 0), new Vector2(100, 100), 2, ColorF.White);
        renderer.DrawPolygon(
            [new Vector2(0, 0), new Vector2(20, 0), new Vector2(20, 20)], ColorF.White);
        renderer.DrawRect(new RectF(10, 10, 40, 40), ColorF.Black);
        renderer.End();
    }
}
