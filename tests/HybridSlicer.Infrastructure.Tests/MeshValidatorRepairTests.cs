using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

public class MeshValidatorRepairTests
{
    private static byte[] BuildStlBinary(Vector3[] verts)
    {
        int triCount = verts.Length / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++)
        {
            var v0 = verts[t * 3]; var v1 = verts[t * 3 + 1]; var v2 = verts[t * 3 + 2];
            var n = Vector3.Cross(v1 - v0, v2 - v0);
            float len = n.Length();
            if (len > 1e-6f) n /= len; else n = Vector3.UnitZ;
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
        return data;
    }

    [Fact]
    public void ValidMesh_PassesValidation()
    {
        // Simple valid triangle
        var verts = new[]
        {
            new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(5, 10, 0),
        };
        var mesh = StlMesh.FromBinary(BuildStlBinary(verts));

        var result = MeshValidator.Validate(mesh);

        result.TriangleCount.Should().Be(1);
        result.DegenerateTriangles.Should().Be(0);
        result.NanInfVertices.Should().Be(0);
    }

    [Fact]
    public void DegenerateTriangle_Detected()
    {
        // Degenerate triangle (all vertices at same point)
        var p = new Vector3(5, 5, 5);
        var verts = new[] { p, p, p };
        var mesh = StlMesh.FromBinary(BuildStlBinary(verts));

        var result = MeshValidator.Validate(mesh);

        result.DegenerateTriangles.Should().Be(1);
    }

    [Fact]
    public void ValidateAndRepair_RemovesDegenerates()
    {
        // Mix of valid and degenerate triangles
        var p = new Vector3(0, 0, 0);
        var verts = new[]
        {
            // Valid triangle
            new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(5, 10, 5),
            // Degenerate (zero area)
            p, p, p,
        };

        var (mesh, result) = MeshValidator.ValidateAndRepair(BuildStlBinary(verts));

        result.Repaired.Should().BeTrue();
        result.TrianglesRemoved.Should().BeGreaterOrEqualTo(1);
        mesh.TriangleCount.Should().Be(1, "degenerate triangle should be removed");
    }

    [Fact]
    public void ValidateAndRepair_ReturnsValidMeshForCleanInput()
    {
        var verts = new[]
        {
            new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(5, 10, 0),
            new Vector3(0, 0, 0), new Vector3(5, 10, 0), new Vector3(0, 10, 5),
        };

        var (mesh, result) = MeshValidator.ValidateAndRepair(BuildStlBinary(verts));

        mesh.TriangleCount.Should().Be(2);
        result.TrianglesRemoved.Should().Be(0);
    }
}
