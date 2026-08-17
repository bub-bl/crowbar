using System.Numerics;
using Crowbar.Engine;
using Crowbar.Engine.Rendering2D;
using Xunit;

namespace Crowbar.Engine.Tests;

/// <summary>
/// CPU-side tests of the border styles (solid / dashed / dotted / double) that
/// the SDF pipeline now supports. They verify the recorded instance encoding
/// (style + dash length in <c>Params.z/w</c>) and that the WGSL carries the
/// outline/dash helpers the fragment shader depends on.
/// </summary>
public class Renderer2DBorderTests
{
    [Fact]
    public void DashedBorder_RecordsStyleAndDashLength()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawRoundedRect(new RectF(0, 0, 40, 20), 4, 2, ColorF.White, BorderStyle.Dashed, 6f);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal(2f, instance.Params.Y); // stroke width
        Assert.Equal(1f, instance.Params.Z); // dashed
        Assert.Equal(6f, instance.Params.W); // dash length
    }

    [Fact]
    public void DottedAndDouble_RecordTheirStyleValues()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawRoundedRect(new RectF(0, 0, 40, 20), 4, 2, ColorF.White, BorderStyle.Dotted, 4f);
        renderer.DrawRoundedRect(new RectF(50, 0, 40, 20), 4, 2, ColorF.White, BorderStyle.Double);
        renderer.End();

        Assert.Equal(2, renderer.Instances.Count);
        Assert.Equal(2f, renderer.Instances[0].Params.Z); // dotted
        Assert.Equal(4f, renderer.Instances[0].Params.W); // dash length
        Assert.Equal(3f, renderer.Instances[1].Params.Z); // double
    }

    [Fact]
    public void SolidStroke_RecordsStyleZeroAndDefaultDash()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawRoundedRect(new RectF(0, 0, 40, 20), 4, 2, ColorF.White);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal(0f, instance.Params.Z); // solid
        Assert.Equal(8f, instance.Params.W); // default dash length
    }

    [Fact]
    public void RectBorder_EmitsRectWithZeroRadius()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawRect(new RectF(10, 10, 30, 20), 3, ColorF.White, BorderStyle.Dashed, 5f);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal(0f, instance.Flags.X);  // rect kind
        Assert.Equal(0f, instance.Params.X); // radius 0
        Assert.Equal(3f, instance.Params.Y); // stroke width
        Assert.Equal(1f, instance.Params.Z); // dashed
        Assert.Equal(5f, instance.Params.W); // dash length
    }

    [Fact]
    public void DashedLine_RecordsStyleAndDashLength()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawLine(new Vector2(0, 0), new Vector2(50, 0), 2, ColorF.White, BorderStyle.Dotted, 7f);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal(3f, instance.Flags.X);  // line kind
        Assert.Equal(2f, instance.Params.Y); // width
        Assert.Equal(2f, instance.Params.Z); // dotted
        Assert.Equal(7f, instance.Params.W); // dash length
    }

    [Fact]
    public void BorderedShapes_BatchIntoOneDraw()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawRect(new RectF(0, 0, 20, 20), 2, ColorF.White, BorderStyle.Dashed);
        renderer.DrawRoundedRect(new RectF(30, 0, 20, 20), 4, 2, ColorF.White, BorderStyle.Double);
        renderer.DrawCircle(new Vector2(60, 10), 10, 2, ColorF.White, BorderStyle.Dotted);
        renderer.End();

        Assert.Equal(3, renderer.InstanceCount);
        var command = Assert.Single(renderer.Commands);
        Assert.Equal(BatchKind.Sdf, command.Kind);
        Assert.Equal(3, command.Count);
    }

    [Fact]
    public void Fill_IgnoresBorderStyle_ButRecordsDefaults()
    {
        var renderer = new Renderer2D();
        renderer.Begin(100, 100);
        renderer.DrawRect(new RectF(0, 0, 20, 20), ColorF.White);
        renderer.End();

        var instance = Assert.Single(renderer.Instances);
        Assert.Equal(0f, instance.Params.Y); // fill (no stroke)
        Assert.Equal(0f, instance.Params.Z); // solid
    }

    [Fact]
    public void Shader_ContainsOutlineAndDashHelpers()
    {
        var source = Shader.Load("Shaders/Ui/Shape.wgsl").Source;
        Assert.Contains("Sdf_RectOutlineDistance", source);
        Assert.Contains("Sdf_EllipseOutlineDistance", source);
        Assert.Contains("Sdf_LineOutlineDistance", source);
        Assert.Contains("Sdf_Dash", source);
    }
}
