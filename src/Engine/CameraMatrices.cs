using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// Per-frame camera matrices shared by the renderer, the viewport gizmos and
/// CPU picking. Computed once from the camera and the render viewport size so
/// the aspect ratio and projection are never re-derived independently — a
/// source of subtle drift between the GPU scene and the CPU ray/picking.
/// </summary>
public readonly record struct CameraMatrices(Matrix4x4 View, Matrix4x4 Projection, float Aspect, int Width, int Height)
{
    /// <summary>Builds the matrices for a render viewport of the given pixel size.</summary>
    public static CameraMatrices Compute(Camera camera, int width, int height)
    {
        var aspect = Math.Max(1, width) / (float)Math.Max(1, height);
        return new CameraMatrices(camera.ViewMatrix, camera.ProjectionMatrix(aspect), aspect, width, height);
    }

    /// <summary>Unprojects a pixel position (top-left origin, viewport-local) into a world ray.</summary>
    public Ray RayFromScreen(Vector2 screenPixels) => Ray.FromScreen(screenPixels, Width, Height, View, Projection);
}
