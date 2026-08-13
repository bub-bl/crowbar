using System.Numerics;
using Crowbar.Engine;
using Crowbar.Engine.Rendering2D;
using Xunit;

namespace Crowbar.Engine.Tests;

public class Renderer2DFilterTests
{
    [Fact]
    public void PushFilter_RedirectsDrawsToChildLayer()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawRect(new RectF(0, 0, 10, 10), ColorF.White);
        renderer.PushFilter(Filter2D.Create(FilterOp.Blur(4f)));
        renderer.DrawRect(new RectF(20, 20, 10, 10), ColorF.FromRgba(255, 0, 0));
        renderer.PopFilter();
        renderer.DrawRect(new RectF(40, 40, 10, 10), ColorF.FromRgba(0, 0, 255));
        renderer.End();

        Assert.Equal(0, renderer.FilterDepth);
        Assert.Equal(2, renderer.InstanceCount);            // two root rects
        Assert.Equal(3, renderer.Commands.Count);           // Sdf, FilterBlit, Sdf
        Assert.Equal(BatchKind.Sdf, renderer.Commands[0].Kind);
        Assert.Equal(BatchKind.FilterBlit, renderer.Commands[1].Kind);
        Assert.Equal(BatchKind.Sdf, renderer.Commands[2].Kind);

        var layer = Assert.Single(renderer.FilterLayers);
        Assert.Equal(1, layer.Instances.Count);             // one child rect
    }

    [Fact]
    public void FilterDepth_TracksNesting()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        Assert.Equal(0, renderer.FilterDepth);
        renderer.PushFilter(Filter2D.Create(FilterOp.Blur(2f)));
        Assert.Equal(1, renderer.FilterDepth);
        renderer.PushFilter(Filter2D.Create(FilterOp.Brightness(2f)));
        Assert.Equal(2, renderer.FilterDepth);
        renderer.PopFilter();
        Assert.Equal(1, renderer.FilterDepth);
        renderer.PopFilter();
        Assert.Equal(0, renderer.FilterDepth);
        renderer.End();
    }

    [Fact]
    public void NestedFilters_ProduceTwoLayers()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.PushFilter(Filter2D.Create(FilterOp.Blur(2f)));
        renderer.DrawRect(new RectF(0, 0, 10, 10), ColorF.White);
        renderer.PushFilter(Filter2D.Create(FilterOp.Brightness(2f)));
        renderer.DrawRect(new RectF(20, 20, 10, 10), ColorF.FromRgba(255, 0, 0));
        renderer.PopFilter();
        renderer.PopFilter();
        renderer.End();

        Assert.Equal(2, renderer.FilterLayers.Count);
        // Layer 0 (innermost) holds the red rect, blitted through brightness.
        Assert.Equal(1, renderer.FilterLayers[0].Instances.Count);
        Assert.Equal(FilterOpKind.Brightness, renderer.FilterLayers[0].Filter.Ops[0].Kind);
        // Layer 1 (outer) holds the white rect and a blit of layer 0, through blur.
        Assert.Equal(1, renderer.FilterLayers[1].Instances.Count);
        Assert.Equal(FilterOpKind.Blur, renderer.FilterLayers[1].Filter.Ops[0].Kind);
        Assert.Contains(renderer.FilterLayers[1].Commands, c => c.Kind == BatchKind.FilterBlit);
    }

    [Fact]
    public void EncodeFilterParams_EncodesBlurAndColorOps()
    {
        var filter = Filter2D.Create(FilterOp.Blur(4f), FilterOp.Brightness(1.5f), FilterOp.Grayscale(0.5f));
        var parameters = Renderer2D.EncodeFilterParams(filter);

        Assert.Equal(4f, parameters.Blur.X);
        Assert.Equal(3f, parameters.OpCount.X);
        Assert.Equal(new Vector4(0, 4, 0, 0), parameters.Op0);    // blur
        Assert.Equal(new Vector4(1, 1.5f, 0, 0), parameters.Op1); // brightness
        Assert.Equal(new Vector4(3, 0.5f, 0, 0), parameters.Op2); // grayscale
    }

    [Fact]
    public void EncodeFilterParams_CapsAtEightOps()
    {
        var filter = Filter2D.Create(
            FilterOp.Blur(1), FilterOp.Blur(2), FilterOp.Blur(3), FilterOp.Blur(4),
            FilterOp.Blur(5), FilterOp.Blur(6), FilterOp.Blur(7), FilterOp.Blur(8), FilterOp.Blur(9));
        var parameters = Renderer2D.EncodeFilterParams(filter);
        Assert.Equal(8f, parameters.OpCount.X);
    }

    [Fact]
    public void Filter2D_StructuralEquality()
    {
        var a = Filter2D.Create(FilterOp.Blur(2f), FilterOp.Invert(1f));
        var b = Filter2D.Create(FilterOp.Blur(2f), FilterOp.Invert(1f));
        var c = Filter2D.Create(FilterOp.Blur(3f), FilterOp.Invert(1f));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.True(Filter2D.None.IsNone);
        Assert.False(a.IsNone);
    }

    [Fact]
    public void FilterShader_ExposesExpectedBindings()
    {
        var shader = Shader.Load("Shaders/Filter.wgsl");
        Assert.Contains(shader.EntryPoints, e => e.Name == "vs_main");
        Assert.Contains(shader.EntryPoints, e => e.Name == "fs_main");
        Assert.Equal(3, shader.Bindings.Count);
        Assert.Contains(shader.Bindings, b => b.Slot == 0 && b.Kind == ShaderBindingKind.Texture);
        Assert.Contains(shader.Bindings, b => b.Slot == 1 && b.Kind == ShaderBindingKind.Sampler);
        Assert.Contains(shader.Bindings, b => b.Slot == 2 && b.Kind == ShaderBindingKind.ReadOnlyStorageBuffer);
    }
}
