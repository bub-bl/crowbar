using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine.Tests;

public class RotationGizmoTests
{
    private const int Width = 1280;
    private const int Height = 720;

    [Fact]
    public void Hover_DetectsTheAxisRingUnderTheMouse()
    {
        var transform = CreateTarget();
        var gizmo = new RotationGizmo();
        gizmo.SetTarget(transform);

        var camera = new Camera();
        // (0, r, 0) lies on the X ring (the circle of radius r in the YZ plane).
        var (ray, pixels) = RayThroughPoint(new Vector3(0f, RingRadius(gizmo, camera), 0f), camera);

        gizmo.UpdateHover(ray, pixels, camera.ViewMatrix, camera.ProjectionMatrix(Aspect), Width, Height);

        Assert.Equal(GizmoAxis.X, gizmo.HoveredAxis);
    }

    [Fact]
    public void Hover_IgnoresPointsFarFromEveryRing()
    {
        var transform = CreateTarget();
        var gizmo = new RotationGizmo();
        gizmo.SetTarget(transform);

        var camera = new Camera();
        // 3× the drawn radius is far outside every ring.
        var (ray, pixels) = RayThroughPoint(new Vector3(RingRadius(gizmo, camera) * 3f, 0f, 0f), camera);

        gizmo.UpdateHover(ray, pixels, camera.ViewMatrix, camera.ProjectionMatrix(Aspect), Width, Height);

        Assert.Equal(GizmoAxis.None, gizmo.HoveredAxis);
    }

    [Fact]
    public void Drag_RotatesTheTargetAroundTheActiveAxis()
    {
        var transform = CreateTarget();
        var gizmo = new RotationGizmo();
        gizmo.SetTarget(transform);

        var camera = new Camera();
        var view = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix(Aspect);

        var (startRay, startPixels) = RayThroughPoint(new Vector3(0f, RingRadius(gizmo, camera), 0f), camera);
        gizmo.UpdateHover(startRay, startPixels, view, projection, Width, Height);
        Assert.Equal(GizmoAxis.X, gizmo.HoveredAxis);
        gizmo.BeginDrag(startRay);

        // Dragging from +Y to +Z sweeps +90° around the X axis (right-hand rule).
        var (dragRay, _) = RayThroughPoint(new Vector3(0f, 0f, RingRadius(gizmo, camera)), camera);
        gizmo.Drag(dragRay);

        Assert.Equal(90f, transform.World.Rotation.Pitch(), 3);
        Assert.Equal(0f, transform.World.Rotation.Yaw(), 3);
        Assert.Equal(0f, transform.World.Rotation.Roll(), 3);
    }

    [Fact]
    public void Drag_SnapsTheAngleToTheDegreeStep()
    {
        var transform = CreateTarget();
        var gizmo = new RotationGizmo { SnapSize = 100f };
        gizmo.SetTarget(transform);

        var camera = new Camera();
        var view = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix(Aspect);

        var (startRay, startPixels) = RayThroughPoint(new Vector3(0f, RingRadius(gizmo, camera), 0f), camera);
        gizmo.UpdateHover(startRay, startPixels, view, projection, Width, Height);
        gizmo.BeginDrag(startRay);

        // 90° of rotation rounds up to the 100° step.
        var (dragRay, _) = RayThroughPoint(new Vector3(0f, 0f, RingRadius(gizmo, camera)), camera);
        gizmo.Drag(dragRay);

        Assert.Equal(100f, transform.World.Rotation.Pitch(), 3);
    }

    [Fact]
    public void Hover_FollowsTheDrawnRingSizeInsteadOfAFixedWorldRadius()
    {
        var transform = CreateTarget();
        var gizmo = new RotationGizmo();
        gizmo.SetTarget(transform);

        // The drawn ring spans a constant screen size (100 px), so its world
        // radius shrinks as the camera closes in. With the camera closer than
        // the default, the drawn radius is well under the 1-unit radius the
        // hover used to test against (regression: the highlight lit up beside
        // the ring). A mouse on the drawn ring must hover...
        var camera = new Camera { Position = new Vector3(2.12f, 1.5f, 2.12f) };
        var view = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix(Aspect);
        var radius = RingRadius(gizmo, camera);
        Assert.True(radius < 1f, $"Drawn ring radius {radius} should be under 1 unit at this distance.");

        var (onRing, onRingPixels) = RayThroughPoint(new Vector3(0f, radius, 0f), camera);
        gizmo.UpdateHover(onRing, onRingPixels, view, projection, Width, Height);
        Assert.Equal(GizmoAxis.X, gizmo.HoveredAxis);

        // ...while a mouse on the old fixed 1-unit ring must not.
        var (oldRing, oldRingPixels) = RayThroughPoint(new Vector3(0f, 1f, 0f), camera);
        gizmo.UpdateHover(oldRing, oldRingPixels, view, projection, Width, Height);
        Assert.Equal(GizmoAxis.None, gizmo.HoveredAxis);
    }

    [Fact]
    public void EndDrag_ReleasesTheActiveAxis()
    {
        var transform = CreateTarget();
        var gizmo = new RotationGizmo();
        gizmo.SetTarget(transform);

        var camera = new Camera();
        var view = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix(Aspect);
        var (ray, pixels) = RayThroughPoint(new Vector3(0f, RingRadius(gizmo, camera), 0f), camera);
        gizmo.UpdateHover(ray, pixels, view, projection, Width, Height);
        gizmo.BeginDrag(ray);

        Assert.True(gizmo.IsDragging);
        gizmo.EndDrag();
        Assert.False(gizmo.IsDragging);
        Assert.Equal(GizmoAxis.None, gizmo.ActiveAxis);
    }

    private static float Aspect => Width / (float)Height;

    /// <summary>The world-space ring radius the renderer draws for this camera (constant screen size).</summary>
    private static float RingRadius(RotationGizmo gizmo, Camera camera) =>
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
