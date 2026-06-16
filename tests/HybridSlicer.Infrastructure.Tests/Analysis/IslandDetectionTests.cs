using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

/// <summary>
/// Tests that island and minima detection forces support points
/// on geometry that a pure normal-angle test would miss.
/// </summary>
public class IslandDetectionTests
{
    /// <summary>
    /// Create a mesh with two boxes: a base box on the plate and a floating box above it.
    /// The floating box should get support points from island detection.
    /// </summary>
    private static StlMesh CreateFloatingIslandMesh()
    {
        // Base box: z[0,3] at center, small
        // Floating box: z[30,33] at center, same size but disconnected
        var verts = new List<Vector3>();
        void AddBox(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            // 6 faces, 2 triangles each = 12 triangles
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                verts.Add(a); verts.Add(b); verts.Add(c);
                verts.Add(a); verts.Add(c); verts.Add(d);
            }
            var v000 = new Vector3(minX, minY, minZ); var v100 = new Vector3(maxX, minY, minZ);
            var v010 = new Vector3(minX, maxY, minZ); var v110 = new Vector3(maxX, maxY, minZ);
            var v001 = new Vector3(minX, minY, maxZ); var v101 = new Vector3(maxX, minY, maxZ);
            var v011 = new Vector3(minX, maxY, maxZ); var v111 = new Vector3(maxX, maxY, maxZ);
            Quad(v001, v101, v111, v011); // top
            Quad(v000, v010, v110, v100); // bottom
            Quad(v100, v110, v111, v101); // right
            Quad(v000, v001, v011, v010); // left
            Quad(v010, v011, v111, v110); // front
            Quad(v000, v100, v101, v001); // back
        }

        AddBox(-5, -5, 0, 5, 5, 3);      // base box on plate
        AddBox(-5, -5, 30, 5, 5, 33);     // floating box

        int triCount = verts.Count / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++)
        {
            // Compute normal from winding
            var v0 = verts[t * 3]; var v1 = verts[t * 3 + 1]; var v2 = verts[t * 3 + 2];
            var n = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
            if (float.IsNaN(n.X)) n = Vector3.UnitZ;
            BitConverter.GetBytes(n.X).CopyTo(data, off);
            BitConverter.GetBytes(n.Y).CopyTo(data, off + 4);
            BitConverter.GetBytes(n.Z).CopyTo(data, off + 8);
            off += 12;
            for (int v = 0; v < 3; v++)
            {
                BitConverter.GetBytes(verts[t * 3 + v].X).CopyTo(data, off);
                BitConverter.GetBytes(verts[t * 3 + v].Y).CopyTo(data, off + 4);
                BitConverter.GetBytes(verts[t * 3 + v].Z).CopyTo(data, off + 8);
                off += 12;
            }
            off += 2;
        }
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void FloatingIsland_GetsSupport()
    {
        var mesh = CreateFloatingIslandMesh();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            UnifiedIslandDetection = true,
        });

        // The floating box at z=30..33 should get at least one support point
        // reaching from z≈30 down to the plate
        result.ValidSupports.Should().BeGreaterThan(0, "floating island needs support");

        // At least one support should have its contact point in the z=28..34 range
        // (near the floating box's underside)
        bool hasIslandSupport = result.LegacySupports.Any(s =>
            s.Segments.Any(seg => seg.Z1 > 25 || seg.Z2 > 25));
        hasIslandSupport.Should().BeTrue("should have a support reaching the floating island");
    }

    [Fact]
    public void FloatingIsland_NoElementAbovePlateWithoutPath()
    {
        var mesh = CreateFloatingIslandMesh();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            UnifiedIslandDetection = true,
        });

        // No slice element should have both endpoints above the plate without
        // being part of a connected support chain
        foreach (var elem in result.SliceElements)
        {
            float minZ = Math.Min(elem.PointA.Z, elem.PointB.Z);
            // Raft/interconnect/pinhead elements can be above plate (connected to pillars)
            if (elem.Type is "raft" or "interconnect" or "pinhead") continue;
            // Pillar/bridge/junction elements above 1mm should connect downward
            if (minZ > 1.0f)
            {
                bool hasLowerElement = result.SliceElements.Any(other =>
                    other != elem &&
                    Vector2.Distance(
                        new Vector2(elem.PointB.X, elem.PointB.Y),
                        new Vector2(other.PointA.X, other.PointA.Y)) < 3.0f &&
                    Math.Min(other.PointA.Z, other.PointB.Z) < minZ);
                hasLowerElement.Should().BeTrue(
                    $"element {elem.Type} at z={minZ:F1} should chain to plate");
            }
        }
    }

    [Fact]
    public void RecomputeNormals_ProducesConsistentResult()
    {
        var mesh = CreateFloatingIslandMesh();
        var recomputed = mesh.RecomputeNormals();

        recomputed.TriangleCount.Should().Be(mesh.TriangleCount);
        recomputed.Vertices.Length.Should().Be(mesh.Vertices.Length);

        // Recomputed normals should all be unit vectors
        for (int t = 0; t < recomputed.TriangleCount; t++)
        {
            float len = recomputed.FileNormals[t].Length();
            len.Should().BeApproximately(1.0f, 0.01f,
                $"normal {t} should be unit length");
        }
    }
}
