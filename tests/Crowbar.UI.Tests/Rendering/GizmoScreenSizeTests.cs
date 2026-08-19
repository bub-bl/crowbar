using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine.Tests;

public class GizmoScreenSizeTests
{
    private const int Height = 720;
    private const float Aspect = 16f / 9f;

    [Fact]
    public void ScreenConstantWorldSize_UsesTheForwardDepth()
    {
        // The camera looks at the scene from an oblique angle and the widget
        // sits away from the world origin, so the depth must be measured along
        // the camera's forward axis. Ground truth: the world extent that spans
        // `pixels` on screen at the origin's forward depth.
        var camera = new Camera();
        var origin = new Vector3(2f, 1.5f, -3f);
        var view = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix(Aspect);

        var depth = Math.Max(1e-4f, Vector3.Dot(camera.Forward, origin - camera.Position));
        // FieldOfView is degrees; the screen-size math needs the half-angle in radians.
        var expected = 100f * 2f * depth * MathF.Tan(camera.FieldOfView * MathF.PI / 180f * 0.5f) / Height;

        var actual = Gizmo.ScreenConstantWorldSize(origin, view, projection, Height, 100f);

        Assert.Equal(expected, actual, 4);
    }

    [Fact]
    public void ScreenConstantWorldSize_IsConstantWhileOrbiting()
    {
        // Orbiting keeps the camera at a fixed distance from the pivot (the
        // widget sits at the pivot), so a screen-constant widget must keep the
        // same world size whatever the orbit angle.
        var camera = new Camera();
        var pivot = new Vector3(2f, 1.5f, -3f);
        var distance = camera.Distance;
        var sizes = new List<float>();

        for (var i = 0; i < 8; i++)
        {
            camera.Yaw = -MathF.PI / 4f + i * MathF.Tau / 8f;
            camera.Pitch = -0.42f;
            // Mirror the orbit controller: keep Position on the orbit sphere.
            camera.Position = pivot - camera.Forward * distance;

            var view = camera.ViewMatrix;
            var projection = camera.ProjectionMatrix(Aspect);
            sizes.Add(Gizmo.ScreenConstantWorldSize(pivot, view, projection, Height, 100f));
        }

        // All orbit angles produce the same on-screen widget size.
        var first = sizes[0];
        Assert.All(sizes, size => Assert.Equal(first, size, 3));
    }
}
