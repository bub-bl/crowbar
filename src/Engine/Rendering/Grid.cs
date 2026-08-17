using System.Numerics;
using System.Runtime.InteropServices;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// Configuration for the editor ground grid. The renderer draws it as a
/// fullscreen pass after the world's meshes (it tests mesh depth without
/// writing it, and fades into the distance), so it stays in sync with the
/// scene automatically.
/// </summary>
public sealed class Grid
{
    /// <summary>Optional world-space size. Null means the grid is infinite.</summary>
    public float? Size { get; set; }

    /// <summary>Distance between two grid lines, in world units.</summary>
    public float CellSize { get; set; } = 1f;

    /// <summary>Distance at which the grid lines fade out completely.</summary>
    public float FadeDistance { get; set; } = 50f;

    public Vector4 LineColor { get; set; } = new(0.35f, 0.35f, 0.38f, 1f);
    public Vector4 XAxisColor { get; set; } = new(0.9f, 0.2f, 0.2f, 1f);
    public Vector4 ZAxisColor { get; set; } = new(0.2f, 0.4f, 0.9f, 1f);

    /// <summary>Whether the X (red) and Z (blue) axes are highlighted.</summary>
    public bool ShowAxes { get; set; } = true;

    /// <summary>
    /// Builds the GPU uniforms for a frame. The matrix inverses are derived
    /// here so callers only need the same view/projection the scene uses.
    /// </summary>
    public GridUniforms CreateUniforms(Matrix4x4 view, Matrix4x4 projection)
    {
        Matrix4x4.Invert(view, out var viewInverse);
        Matrix4x4.Invert(projection, out var projectionInverse);

        return new GridUniforms
        {
            View = view,
            Proj = projection,
            ViewInv = viewInverse,
            ProjInv = projectionInverse,
            Settings = new Vector4(
                Size.HasValue ? MathF.Max(Size.Value, 0.01f) : 0f,
                MathF.Max(CellSize, 0.001f),
                MathF.Max(FadeDistance, 0.01f),
                ShowAxes ? 1f : 0f),
            LineColor = LineColor,
            XAxisColor = XAxisColor,
            ZAxisColor = ZAxisColor
        };
    }
}

/// <summary>
/// Mirrors GridUniforms in Shaders/Editor/Grid.slang (view/proj + inverses, settings
/// and colors). Engine-owned, so the layout is declared once here and once in
/// the shader, like the scene and lights uniforms.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GridUniforms
{
    public Matrix4x4 View;
    public Matrix4x4 Proj;
    public Matrix4x4 ViewInv;
    public Matrix4x4 ProjInv;
    public Vector4 Settings;
    public Vector4 LineColor;
    public Vector4 XAxisColor;
    public Vector4 ZAxisColor;
}
