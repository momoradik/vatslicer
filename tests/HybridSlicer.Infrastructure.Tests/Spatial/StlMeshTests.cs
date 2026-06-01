using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

/// <summary>
/// Tests for StlMesh parsing and transformation.
/// </summary>
public class StlMeshTests
{
    [Fact]
    public void FromBinary_ValidStl_ParsesCorrectly()
    {
        var verts = new Vector3[]
        {
            new(0,0,0), new(1,0,0), new(0,1,0),
            new(0,0,0), new(0,1,0), new(0,0,1),
        };
        var data = BuildStl(verts);
        var mesh = StlMesh.FromBinary(data);

        mesh.TriangleCount.Should().Be(2);
        mesh.Vertices.Length.Should().Be(6);
    }

    [Fact]
    public void FromBinary_ComputesBounds()
    {
        var verts = new Vector3[]
        {
            new(-5, -3, 0), new(10, 0, 0), new(0, 8, 15),
        };
        var mesh = StlMesh.FromBinary(BuildStl(verts));

        mesh.Min.X.Should().BeApproximately(-5, 0.01f);
        mesh.Min.Y.Should().BeApproximately(-3, 0.01f);
        mesh.Min.Z.Should().BeApproximately(0, 0.01f);
        mesh.Max.X.Should().BeApproximately(10, 0.01f);
        mesh.Max.Y.Should().BeApproximately(8, 0.01f);
        mesh.Max.Z.Should().BeApproximately(15, 0.01f);
    }

    [Fact]
    public void Transform_Translate_ShiftsBounds()
    {
        var verts = new Vector3[] { new(0,0,0), new(1,0,0), new(0,1,0) };
        var mesh = StlMesh.FromBinary(BuildStl(verts));

        var transformed = mesh.Transform(new Vector3(10, 20, 30), 1f);

        transformed.Min.X.Should().BeApproximately(10, 0.01f);
        transformed.Min.Y.Should().BeApproximately(20, 0.01f);
        transformed.Min.Z.Should().BeApproximately(30, 0.01f);
    }

    [Fact]
    public void Transform_Scale_ScalesBounds()
    {
        var verts = new Vector3[] { new(0,0,0), new(10,0,0), new(0,10,0) };
        var mesh = StlMesh.FromBinary(BuildStl(verts));

        var scaled = mesh.Transform(Vector3.Zero, 2f);

        scaled.Max.X.Should().BeApproximately(20, 0.01f);
        scaled.Max.Y.Should().BeApproximately(20, 0.01f);
    }

    [Fact]
    public void Transform_PreservesTriangleCount()
    {
        var verts = new Vector3[]
        {
            new(0,0,0), new(1,0,0), new(0,1,0),
            new(1,0,0), new(1,1,0), new(0,1,0),
        };
        var mesh = StlMesh.FromBinary(BuildStl(verts));
        var transformed = mesh.Transform(new Vector3(5, 5, 5), 3f);

        transformed.TriangleCount.Should().Be(mesh.TriangleCount);
    }

    [Fact]
    public void FromBinary_TooSmall_Throws()
    {
        var act = () => StlMesh.FromBinary(new byte[10]);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FromBinary_ClaimsMoreTrianglesThanData_Throws()
    {
        var data = new byte[84 + 10]; // header + partial triangle
        BitConverter.GetBytes((uint)100).CopyTo(data, 80); // claims 100 triangles
        var act = () => StlMesh.FromBinary(data);
        act.Should().Throw<InvalidOperationException>();
    }

    private static byte[] BuildStl(Vector3[] verts)
    {
        int triCount = verts.Length / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++)
        {
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
}
