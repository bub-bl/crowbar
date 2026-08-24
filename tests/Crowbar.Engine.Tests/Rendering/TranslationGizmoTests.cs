using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine.Tests;

public class TranslationGizmoTests
{
    private const int Width = 1280;
    private const int Height = 720;

    [Fact]
    public void Hover_DetectsTheAxisUnderTheMouse()
    {
        var transform = CreateTarget();
        var gizmo = new TranslationGizmo();
        gizmo.SetTarget(transform);

        var camera = new Camera();
        var (ray, pixels) = RayThroughPoint(new Vector3(ShaftLength(gizmo, camera) * 0.8f, 0f, 0f), camera);

        gizmo.UpdateHover(ray, pixels, camera.ViewMatrix, camera.ProjectionMatrix(Aspect), Width, Height);

        Assert.Equal(GizmoAxis.X, gizmo.HoveredAxis);
    }

    [Fact]
    public void Hover_IgnoresPointsBeyondTheShaft()
    {
        var transform = CreateTarget();
        var gizmo = new TranslationGizmo();
        gizmo.SetTarget(transform);

        var camera = new Camera();
        // On the X axis line but far beyond the drawn shaft.
        var (ray, pixels) = RayThroughPoint(new Vector3(ShaftLength(gizmo, camera) * 5f, 0f, 0f), camera);

        gizmo.UpdateHover(ray, pixels, camera.ViewMatrix, camera.ProjectionMatrix(Aspect), Width, Height);

        Assert.Equal(GizmoAxis.None, gizmo.HoveredAxis);
    }

    [Fact]
    public void Drag_MovesTheTargetAlongTheActiveAxis()
    {
        var transform = CreateTarget();
        var gizmo = new TranslationGizmo();
        gizmo.SetTarget(transform);

        var camera = new Camera();
        var view = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix(Aspect);

        var (startRay, startPixels) = RayThroughPoint(new Vector3(ShaftLength(gizmo, camera) * 0.6f, 0f, 0f), camera);
        gizmo.UpdateHover(startRay, startPixels, view, projection, Width, Height);
        Assert.Equal(GizmoAxis.X, gizmo.HoveredAxis);
        gizmo.BeginDrag(startRay);

        var (dragRay, _) = RayThroughPoint(new Vector3(ShaftLength(gizmo, camera) * 0.6f + 0.8f, 0f, 0f), camera);
        gizmo.Drag(dragRay);

        // The anchor moved 0.8 units along +X; the entity follows exactly.
        Assert.Equal(0.8f, transform.World.Position.X, 3);
        Assert.Equal(0f, transform.World.Position.Y, 3);
        Assert.Equal(0f, transform.World.Position.Z, 3);
    }

    [Fact]
    public void Drag_SnapsTheMovementToTheSnapSize()
    {
        var transform = CreateTarget();
        var gizmo = new TranslationGizmo { SnapSize = 1f };
        gizmo.SetTarget(transform);

        var camera = new Camera();
        var view = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix(Aspect);

        var (startRay, startPixels) = RayThroughPoint(new Vector3(ShaftLength(gizmo, camera) * 0.6f, 0f, 0f), camera);
        gizmo.UpdateHover(startRay, startPixels, view, projection, Width, Height);
        gizmo.BeginDrag(startRay);

        // 0.6 world units of movement rounds up to the 1-unit cell.
        var (dragRay, _) = RayThroughPoint(new Vector3(ShaftLength(gizmo, camera) * 0.6f + 0.6f, 0f, 0f), camera);
        gizmo.Drag(dragRay);

        Assert.Equal(1f, transform.World.Position.X, 3);
        Assert.Equal(0f, transform.World.Position.Y, 3);
        Assert.Equal(0f, transform.World.Position.Z, 3);
    }

    [Fact]
    public void LocalAxes_FollowTheTargetRotation()
    {
        var transform = CreateTarget();
        transform.Local = new Transform(Vector3.Zero, Rotation.FromYaw(90f), Vector3.One);

        var gizmo = new TranslationGizmo();
        gizmo.SetTarget(transform);
        AssertEqual(gizmo.AxisDirection(GizmoAxis.X), 1f, 0f, 0f);

        // With local axes, a 90 degree yaw maps +X to -Z and +Z to +X.
        gizmo.LocalAxes = true;
        gizmo.SetTarget(transform);
        AssertEqual(gizmo.AxisDirection(GizmoAxis.X), 0f, 0f, -1f);
        AssertEqual(gizmo.AxisDirection(GizmoAxis.Z), 1f, 0f, 0f);
    }

    [Fact]
    public void EndDrag_ReleasesTheActiveAxis()
    {
        var transform = CreateTarget();
        var gizmo = new TranslationGizmo();
        gizmo.SetTarget(transform);

        var camera = new Camera();
        var view = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix(Aspect);
        var (ray, pixels) = RayThroughPoint(new Vector3(ShaftLength(gizmo, camera) * 0.6f, 0f, 0f), camera);
        gizmo.UpdateHover(ray, pixels, view, projection, Width, Height);
        gizmo.BeginDrag(ray);

        Assert.True(gizmo.IsDragging);
        gizmo.EndDrag();
        Assert.False(gizmo.IsDragging);
        Assert.Equal(GizmoAxis.None, gizmo.ActiveAxis);
    }

    private static float Aspect => Width / (float)Height;

    /// <summary>The world-space shaft length the renderer draws for this camera (constant screen size).</summary>
    private static float ShaftLength(TranslationGizmo gizmo, Camera camera) =>
        Gizmo.ScreenConstantWorldSize(Vector3.Zero, camera.ViewMatrix, camera.ProjectionMatrix(Aspect), Height, gizmo.ScreenSizePx);

    private static MeshRenderer CreateTarget()
    {
        using var world = new World();
        var level = world.CreateLevel("Test");
        var entity = level.SpawnEntity("Target");
        var renderer = entity.AddComponent<MeshRenderer>();
        renderer.Local = new Transform(Vector3.Zero, Rotation.Identity, Vector3.One);
        return renderer;
    }

    private static void AssertEqual(Vector3 actual, float x, float y, float z)
    {
        Assert.Equal(x, actual.X, 3);
        Assert.Equal(y, actual.Y, 3);
        Assert.Equal(z, actual.Z, 3);
    }

    /// <summary>Builds the mouse ray whose pixel lies exactly on the given world point's projection.</summary>
    private static (Ray Ray, Vector2 Pixels) RayThroughPoint(Vector3 world, Camera camera)
    {
        var view = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix(Aspect);
        var clip = Vector4.Transform(new Vector4(world, 1f), view * projection);
        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        var pixels = new Vector2(
            (ndc.X * 0.5f + 0.5f) * Width,
            (1f - (ndc.Y * 0.5f + 0.5f)) * Height);
        return (Ray.FromScreen(pixels, Width, Height, view, projection), pixels);
    }
}
