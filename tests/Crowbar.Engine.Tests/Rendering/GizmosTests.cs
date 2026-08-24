using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine.Tests;

public class GizmosTests
{
    [Fact]
    public void Begin_ScopesDrawCallsAndRestoresThePreviousBatch()
    {
        var batch = new GizmoLineBatch();

        using (Gizmos.Begin(batch))
        {
            Gizmos.DrawLine(Vector3.Zero, Vector3.UnitX);
            Assert.Equal(1, batch.Count);
        }

        // Outside the scope there is no batch, so draw calls are no-ops.
        Gizmos.DrawLine(Vector3.Zero, Vector3.UnitY);
        Assert.Equal(1, batch.Count);
    }

    [Fact]
    public void Begin_ScopesNestAndRestoreTheOuterBatch()
    {
        var outer = new GizmoLineBatch();
        var inner = new GizmoLineBatch();

        using (Gizmos.Begin(outer))
        {
            Gizmos.DrawLine(Vector3.Zero, Vector3.UnitX);
            using (Gizmos.Begin(inner))
            {
                Gizmos.DrawLine(Vector3.Zero, Vector3.UnitY);
                Assert.Equal(1, inner.Count);
            }

            Gizmos.DrawLine(Vector3.Zero, Vector3.UnitZ);
            Assert.Equal(2, outer.Count);
        }
    }

    [Fact]
    public void Begin_ExposesAndRestoresTheSelectedEntity()
    {
        using var world = new World();
        var entity = world.CreateLevel("Test").SpawnEntity("X");
        var batch = new GizmoLineBatch();

        using (Gizmos.Begin(batch, entity))
            Assert.Same(entity, Gizmos.SelectedEntity);

        Assert.Null(Gizmos.SelectedEntity);
    }

    [Fact]
    public void DrawLine_CapturesTheAmbientColorAndThickness()
    {
        var batch = new GizmoLineBatch();
        using (Gizmos.Begin(batch))
        {
            Gizmos.Color = new Vector4(1f, 0f, 0f, 0.5f);
            Gizmos.LineThickness = 4f;
            Gizmos.DrawLine(new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f));
        }

        var line = Assert.Single(batch.Lines);
        Assert.Equal(new Vector3(1f, 2f, 3f), line.Start);
        Assert.Equal(new Vector3(4f, 5f, 6f), line.End);
        Assert.Equal(new Vector4(1f, 0f, 0f, 0.5f), line.Color);
        Assert.Equal(4f, line.Thickness);
    }

    [Fact]
    public void DrawSphere_EmitsThreeOrthogonalGreatCircles()
    {
        var batch = new GizmoLineBatch();
        using (Gizmos.Begin(batch))
            Gizmos.DrawSphere(new Vector3(1f, 2f, 3f), 2f);

        // 3 circles × 32 segments each.
        Assert.Equal(3 * 32, batch.Count);

        // Every endpoint sits on the sphere surface.
        foreach (var line in batch.Lines)
        {
            Assert.Equal(2f, (line.Start - new Vector3(1f, 2f, 3f)).Length(), 4);
            Assert.Equal(2f, (line.End - new Vector3(1f, 2f, 3f)).Length(), 4);
        }
    }

    [Fact]
    public void DrawCircle_LiesInThePlanePerpendicularToTheNormal()
    {
        var batch = new GizmoLineBatch();
        using (Gizmos.Begin(batch))
            Gizmos.DrawCircle(Vector3.Zero, Vector3.UnitY, 3f, segments: 64);

        // A circle around +Y stays on the XZ plane (y = 0).
        Assert.Equal(64, batch.Count);
        foreach (var line in batch.Lines)
        {
            Assert.Equal(0f, line.Start.Y, 4);
            Assert.Equal(0f, line.End.Y, 4);
            Assert.Equal(3f, line.Start.Length(), 4);
        }
    }

    [Fact]
    public void DrawWireCube_EmitsTwelveEdges()
    {
        var batch = new GizmoLineBatch();
        using (Gizmos.Begin(batch))
            Gizmos.DrawWireCube(Vector3.Zero, new Vector3(2f, 4f, 6f));

        Assert.Equal(12, batch.Count);
    }

    [Fact]
    public void DrawArrow_TerminatesAtTheTip()
    {
        var batch = new GizmoLineBatch();
        var tip = new Vector3(0f, 0f, 3f);
        using (Gizmos.Begin(batch))
            Gizmos.DrawArrow(Vector3.Zero, tip);

        // The arrowhead spokes converge on the tip.
        Assert.Contains(batch.Lines, line => Vector3.Distance(line.End, tip) < 1e-4f);
    }
}
