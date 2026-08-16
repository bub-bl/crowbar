using System.Numerics;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// One gizmo line segment. Thickness is the full on-screen width in pixels,
/// which the renderer expands into a screen-space quad (WebGPU has no wide
/// lines). Color and thickness are captured when the line is added, so every
/// line keeps the state it was drawn with.
/// </summary>
public readonly record struct GizmoLine(Vector3 Start, Vector3 End, Vector4 Color, float Thickness);

/// <summary>
/// CPU-side collection of gizmo line segments. The static
/// <see cref="Gizmos"/> API records into whichever batch its current scope is
/// bound to; the viewport's <see cref="GizmoRenderer"/> owns a reusable batch,
/// clears it each frame, runs every component's gizmo hook inside a scope and
/// uploads the result to the GPU. Shape primitives (spheres, circles, boxes,
/// arrows) expand to line segments here so the batch is trivial to serialize
/// and to test without a graphics device.
/// </summary>
public sealed class GizmoLineBatch
{
    private readonly List<GizmoLine> _lines = [];

    /// <summary>The lines recorded so far, in draw order.</summary>
    public IReadOnlyList<GizmoLine> Lines => _lines;

    public int Count => _lines.Count;

    public void Clear() => _lines.Clear();

    /// <summary>Adds a line segment from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public void AddLine(Vector3 from, Vector3 to, Vector4 color, float thickness = 2f)
    {
        if (thickness <= 0f)
            return;
        _lines.Add(new GizmoLine(from, to, color, thickness));
    }

    /// <summary>Adds a line from <paramref name="from"/> along <paramref name="direction"/> (its length is the direction's length).</summary>
    public void AddRay(Vector3 from, Vector3 direction, Vector4 color, float thickness = 2f) =>
        AddLine(from, from + direction, color, thickness);

    /// <summary>
    /// Adds a circle in the plane perpendicular to <paramref name="normal"/>,
    /// centered on <paramref name="center"/> with the given radius.
    /// </summary>
    public void AddCircle(Vector3 center, Vector3 normal, float radius, Vector4 color, float thickness = 2f, int segments = 32)
    {
        if (radius <= 0f || segments < 3)
            return;

        if (normal.LengthSquared() < 1e-6f)
            return;
        normal = Vector3.Normalize(normal);

        GetPlaneBasis(normal, out var tangent, out var bitangent);
        var step = MathF.Tau / segments;
        for (var i = 0; i < segments; i++)
        {
            var angle0 = i * step;
            var angle1 = (i + 1) * step;
            var p0 = center + (tangent * MathF.Cos(angle0) + bitangent * MathF.Sin(angle0)) * radius;
            var p1 = center + (tangent * MathF.Cos(angle1) + bitangent * MathF.Sin(angle1)) * radius;
            AddLine(p0, p1, color, thickness);
        }
    }

    /// <summary>
    /// Adds a wireframe sphere as three orthogonal great circles (XY, XZ and
    /// YZ planes), the classic minimal wire-sphere silhouette.
    /// </summary>
    public void AddWireSphere(Vector3 center, float radius, Vector4 color, float thickness = 2f, int segments = 32)
    {
        if (radius <= 0f)
            return;

        AddCircle(center, Vector3.UnitX, radius, color, thickness, segments);
        AddCircle(center, Vector3.UnitY, radius, color, thickness, segments);
        AddCircle(center, Vector3.UnitZ, radius, color, thickness, segments);
    }

    /// <summary>Adds the 12 edges of an axis-aligned wireframe box.</summary>
    public void AddWireCube(Vector3 center, Vector3 size, Vector4 color, float thickness = 2f)
    {
        var half = size * 0.5f;
        var nnn = center + new Vector3(-half.X, -half.Y, -half.Z);
        var pnn = center + new Vector3(half.X, -half.Y, -half.Z);
        var npn = center + new Vector3(-half.X, half.Y, -half.Z);
        var ppn = center + new Vector3(half.X, half.Y, -half.Z);
        var nnp = center + new Vector3(-half.X, -half.Y, half.Z);
        var pnp = center + new Vector3(half.X, -half.Y, half.Z);
        var npp = center + new Vector3(-half.X, half.Y, half.Z);
        var ppp = center + new Vector3(half.X, half.Y, half.Z);

        // Back face (-Z) and front face (+Z).
        AddLine(nnn, pnn, color, thickness);
        AddLine(pnn, ppn, color, thickness);
        AddLine(ppn, npn, color, thickness);
        AddLine(npn, nnn, color, thickness);
        AddLine(nnp, pnp, color, thickness);
        AddLine(pnp, ppp, color, thickness);
        AddLine(ppp, npp, color, thickness);
        AddLine(npp, nnp, color, thickness);

        // Connections between the two faces.
        AddLine(nnn, nnp, color, thickness);
        AddLine(pnn, pnp, color, thickness);
        AddLine(ppn, ppp, color, thickness);
        AddLine(npn, npp, color, thickness);
    }

    /// <summary>
    /// Adds a line from <paramref name="from"/> to <paramref name="to"/> with
    /// an arrowhead at <paramref name="to"/>. Non-positive head dimensions
    /// default to fractions of the arrow length.
    /// </summary>
    public void AddArrow(Vector3 from, Vector3 to, Vector4 color, float thickness = 2f,
        float headLength = 0f, float headRadius = 0f, int headSegments = 8)
    {
        var delta = to - from;
        var length = delta.Length();
        if (length < 1e-6f || headSegments < 3)
            return;
        var direction = delta / length;

        if (headLength <= 0f)
            headLength = length * 0.25f;
        if (headRadius <= 0f)
            headRadius = headLength * 0.4f;
        headLength = Math.Min(headLength, length);

        var headBase = to - direction * headLength;
        AddLine(from, headBase, color, thickness);

        GetPlaneBasis(direction, out var tangent, out var bitangent);
        var step = MathF.Tau / headSegments;
        for (var i = 0; i < headSegments; i++)
        {
            var spoke = tangent * MathF.Cos(i * step) + bitangent * MathF.Sin(i * step);
            AddLine(headBase + spoke * headRadius, to, color, thickness);
        }

        AddCircle(headBase, direction, headRadius, color, thickness, headSegments);
    }

    /// <summary>Builds two orthonormal directions spanning the plane perpendicular to <paramref name="normal"/>.</summary>
    private static void GetPlaneBasis(Vector3 normal, out Vector3 tangent, out Vector3 bitangent)
    {
        // Avoid the degenerate case where the reference is parallel to the
        // normal (a vertical normal), mirroring the gizmo shader.
        var reference = MathF.Abs(normal.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        tangent = Vector3.Normalize(Vector3.Cross(normal, reference));
        bitangent = Vector3.Cross(normal, tangent);
    }
}
