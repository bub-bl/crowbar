using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine.Tests;

public class RotationRingGeometryTests
{
    [Fact]
    public void BuildRingVertices_SweepsATubeNotAFlatAnnulus()
    {
        var vertices = GizmoRenderer.BuildRingVertices();

        // 48 ring segments × 8 tube segments × 6 vertices × 4 floats.
        Assert.Equal(48 * 8 * 6 * 4, vertices.Length);

        // The first vertex is at ring angle 0 / tube angle 0: (cos0, sin0, cos0, sin0).
        Assert.Equal(1f, vertices[0], 6);
        Assert.Equal(0f, vertices[1], 6);
        Assert.Equal(1f, vertices[2], 6);
        Assert.Equal(0f, vertices[3], 6);

        // The tube sweeps a full circle, so a quarter turn around the tube
        // (φ = π/2) yields an axial component of 1 — this is the thickness that
        // keeps the ring visible when it is viewed edge-on. A flat annulus
        // would leave this component at 0 for every vertex.
        // Vertex at segment 0, tube 2 (φ = 2·τ/8 = π/2), first corner.
        var axialIndex = (0 * 8 + 2) * 24 + 3;
        Assert.Equal(1f, vertices[axialIndex], 6);

        // Every vertex's ring position is a unit vector on the circle.
        for (var i = 0; i < vertices.Length; i += 4)
        {
            var ring = new Vector2(vertices[i], vertices[i + 1]);
            Assert.Equal(1f, ring.Length(), 5);
        }
    }
}
