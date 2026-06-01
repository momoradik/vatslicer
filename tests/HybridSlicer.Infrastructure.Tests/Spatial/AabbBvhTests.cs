using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

/// <summary>
/// Tests for the AABB BVH acceleration structure.
/// Uses a unit cube (0,0,0)-(1,1,1) as the standard test mesh.
/// </summary>
public class AabbBvhTests
{
    /// <summary>
    /// Creates a unit cube mesh from (0,0,0) to (1,1,1) as 12 triangles.
    /// </summary>
    private static StlMesh CreateCube(float size = 1f, Vector3? offset = null)
    {
        var o = offset ?? Vector3.Zero;
        var s = size;
        // 6 faces x 2 triangles = 12 triangles x 3 vertices = 36 vertices
        var verts = new Vector3[36];
        int vi = 0;

        // Helper: add two triangles for a quad face
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            verts[vi++] = a + o; verts[vi++] = b + o; verts[vi++] = c + o; // tri 1
            verts[vi++] = a + o; verts[vi++] = c + o; verts[vi++] = d + o; // tri 2
        }

        // Front (Z=s), Back (Z=0), Right (X=s), Left (X=0), Top (Y=s), Bottom (Y=0)
        // Winding order: outward-facing normals (counterclockwise when viewed from outside)
        Quad(new(0,0,s), new(s,0,s), new(s,s,s), new(0,s,s)); // front +Z
        Quad(new(0,0,0), new(0,s,0), new(s,s,0), new(s,0,0)); // back -Z
        Quad(new(s,0,0), new(s,0,s), new(s,s,s), new(s,s,0)); // right +X
        Quad(new(0,0,s), new(0,0,0), new(0,s,0), new(0,s,s)); // left -X (reversed)
        Quad(new(0,s,s), new(s,s,s), new(s,s,0), new(0,s,0)); // top +Y
        Quad(new(0,0,0), new(s,0,0), new(s,0,s), new(0,0,s)); // bottom -Y

        var min = new Vector3(0 + o.X, 0 + o.Y, 0 + o.Z);
        var max = new Vector3(s + o.X, s + o.Y, s + o.Z);
        // Use reflection to create StlMesh (internal constructor)
        return CreateMesh(verts, min, max);
    }

    private static StlMesh CreateMesh(Vector3[] vertices, Vector3 min, Vector3 max)
    {
        // StlMesh has an internal constructor. Use the FromBinary path instead.
        // Build a minimal binary STL from vertices.
        int triCount = vertices.Length / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int offset = 84;
        for (int t = 0; t < triCount; t++)
        {
            // Normal (0,0,0) — will be recomputed
            offset += 12;
            for (int v = 0; v < 3; v++)
            {
                BitConverter.GetBytes(vertices[t * 3 + v].X).CopyTo(data, offset);
                BitConverter.GetBytes(vertices[t * 3 + v].Y).CopyTo(data, offset + 4);
                BitConverter.GetBytes(vertices[t * 3 + v].Z).CopyTo(data, offset + 8);
                offset += 12;
            }
            offset += 2; // attribute byte count
        }
        return StlMesh.FromBinary(data);
    }

    // ── Build tests ──────────────────────────────────────────────────────

    [Fact]
    public void Build_UnitCube_Creates12Triangles()
    {
        var mesh = CreateCube();
        var bvh = AabbBvh.Build(mesh);

        bvh.TriangleCount.Should().Be(12);
        bvh.NodeCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Build_LargeMesh_DoesNotCrash()
    {
        // Create a mesh with 1000 triangles (grid of quads)
        var verts = new Vector3[3000];
        int vi = 0;
        for (int i = 0; i < 1000; i++)
        {
            float x = (i % 32) * 1f;
            float y = (i / 32) * 1f;
            verts[vi++] = new Vector3(x, y, 0);
            verts[vi++] = new Vector3(x + 1, y, 0);
            verts[vi++] = new Vector3(x, y + 1, 0);
        }
        var mesh = CreateMesh(verts, new Vector3(0), new Vector3(32, 32, 0));
        var bvh = AabbBvh.Build(mesh);

        bvh.TriangleCount.Should().Be(1000);
        bvh.NodeCount.Should().BeGreaterThan(0);
    }

    // ── RayCast tests ────────────────────────────────────────────────────

    [Fact]
    public void RayCast_RayHitsCubeFront_ReturnsCorrectDistance()
    {
        var mesh = CreateCube();
        var bvh = AabbBvh.Build(mesh);

        // Ray from (0.5, 0.5, -1) in +Z direction should hit front face at Z=0
        var hit = bvh.RayCast(new Vector3(0.5f, 0.5f, -1f), Vector3.UnitZ);

        hit.Should().NotBeNull();
        hit!.Value.Distance.Should().BeApproximately(1f, 0.01f);
        hit.Value.Point.Z.Should().BeApproximately(0f, 0.01f);
    }

    [Fact]
    public void RayCast_RayMissesCube_ReturnsNull()
    {
        var mesh = CreateCube();
        var bvh = AabbBvh.Build(mesh);

        // Ray from (5, 5, -1) in +Z direction — misses the cube entirely
        var hit = bvh.RayCast(new Vector3(5f, 5f, -1f), Vector3.UnitZ);
        hit.Should().BeNull();
    }

    [Fact]
    public void RayCast_RayFromAbove_HitsTopFace()
    {
        var mesh = CreateCube();
        var bvh = AabbBvh.Build(mesh);

        // Ray from (0.5, 2, 0.5) in -Y direction
        var hit = bvh.RayCast(new Vector3(0.5f, 2f, 0.5f), -Vector3.UnitY);

        hit.Should().NotBeNull();
        hit!.Value.Point.Y.Should().BeApproximately(1f, 0.01f);
    }

    [Fact]
    public void RayCast_MaxDistance_LimitRange()
    {
        var mesh = CreateCube();
        var bvh = AabbBvh.Build(mesh);

        // Ray that would hit at distance 1.0 but maxDistance is 0.5
        var hit = bvh.RayCast(new Vector3(0.5f, 0.5f, -1f), Vector3.UnitZ, 0.5f);
        hit.Should().BeNull();
    }

    // ── BeamCast tests ───────────────────────────────────────────────────

    [Fact]
    public void BeamCast_CenterRayHits_ReturnsDistance()
    {
        var mesh = CreateCube();
        var bvh = AabbBvh.Build(mesh);

        float dist = bvh.BeamCast(new Vector3(0.5f, 0.5f, -1f), Vector3.UnitZ, 0.1f, 8);
        dist.Should().BeApproximately(1f, 0.05f);
    }

    [Fact]
    public void BeamCast_WideBeamMisses_ReturnsMaxDistance()
    {
        var mesh = CreateCube();
        var bvh = AabbBvh.Build(mesh);

        // Beam aimed beside the cube
        float dist = bvh.BeamCast(new Vector3(5f, 5f, -1f), Vector3.UnitZ, 0.1f, 8);
        dist.Should().Be(float.MaxValue);
    }

    // ── IsInside tests ───────────────────────────────────────────────────

    [Fact]
    public void IsInside_PointInsideCube_ReturnsTrue()
    {
        var mesh = CreateCube();
        var bvh = AabbBvh.Build(mesh);

        // Exact center should now work with jittered rays
        bvh.IsInside(new Vector3(0.5f, 0.5f, 0.5f)).Should().BeTrue();
    }

    [Fact]
    public void IsInside_PointOutsideCube_ReturnsFalse()
    {
        var mesh = CreateCube();
        var bvh = AabbBvh.Build(mesh);

        bvh.IsInside(new Vector3(2f, 2f, 2f)).Should().BeFalse();
    }

    [Fact]
    public void IsInside_PointBelowCube_ReturnsFalse()
    {
        var mesh = CreateCube();
        var bvh = AabbBvh.Build(mesh);

        bvh.IsInside(new Vector3(0.5f, 0.5f, -1f)).Should().BeFalse();
    }

    [Fact]
    public void IsInside_MultiplePoints_CorrectClassification()
    {
        var mesh = CreateCube(10f);
        var bvh = AabbBvh.Build(mesh);

        // Points clearly inside — including exact positions (jittered rays handle edges)
        bvh.IsInside(new Vector3(5f, 5f, 5f)).Should().BeTrue();
        bvh.IsInside(new Vector3(1f, 1f, 1f)).Should().BeTrue();
        bvh.IsInside(new Vector3(9f, 9f, 9f)).Should().BeTrue();

        // Points clearly outside
        bvh.IsInside(new Vector3(-1f, 5f, 5f)).Should().BeFalse();
        bvh.IsInside(new Vector3(5f, -1f, 5f)).Should().BeFalse();
        bvh.IsInside(new Vector3(5f, 5f, 11f)).Should().BeFalse();
    }

    // ── ClosestPoint tests ───────────────────────────────────────────────

    [Fact]
    public void ClosestPoint_PointAboveCube_ReturnsTopFace()
    {
        var mesh = CreateCube();
        var bvh = AabbBvh.Build(mesh);

        var result = bvh.ClosestPoint(new Vector3(0.5f, 2f, 0.5f));

        result.Should().NotBeNull();
        result!.Value.Point.Y.Should().BeApproximately(1f, 0.01f);
        result.Value.Distance.Should().BeApproximately(1f, 0.01f);
    }

    [Fact]
    public void ClosestPoint_PointInsideCube_ReturnsNearestFace()
    {
        var mesh = CreateCube();
        var bvh = AabbBvh.Build(mesh);

        // Point at (0.5, 0.9, 0.5) — closest to top face (Y=1) at distance 0.1
        var result = bvh.ClosestPoint(new Vector3(0.5f, 0.9f, 0.5f));

        result.Should().NotBeNull();
        result!.Value.Distance.Should().BeLessThan(0.15f);
    }

    [Fact]
    public void ClosestPoint_PointAtCorner_ReturnsCorner()
    {
        var mesh = CreateCube();
        var bvh = AabbBvh.Build(mesh);

        var result = bvh.ClosestPoint(new Vector3(2f, 2f, 2f));

        result.Should().NotBeNull();
        // Closest point should be near corner (1,1,1)
        result!.Value.Point.X.Should().BeApproximately(1f, 0.01f);
        result.Value.Point.Y.Should().BeApproximately(1f, 0.01f);
        result.Value.Point.Z.Should().BeApproximately(1f, 0.01f);
    }
}
