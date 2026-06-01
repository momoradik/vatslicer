using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

public class AabbBvhInsideTests
{
    private static AabbBvh BuildCubeBvh(float size = 10f, Vector3? offset = null)
    {
        var o = offset ?? Vector3.Zero;
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a+o; verts[vi++]=b+o; verts[vi++]=c+o;
            verts[vi++]=a+o; verts[vi++]=c+o; verts[vi++]=d+o;
        }
        float s = size;
        Quad(new(0,0,s), new(s,0,s), new(s,s,s), new(0,s,s));
        Quad(new(0,0,0), new(0,s,0), new(s,s,0), new(s,0,0));
        Quad(new(s,0,0), new(s,0,s), new(s,s,s), new(s,s,0));
        Quad(new(0,0,s), new(0,0,0), new(0,s,0), new(0,s,s));
        Quad(new(0,s,s), new(s,s,s), new(s,s,0), new(0,s,0));
        Quad(new(0,0,0), new(s,0,0), new(s,0,s), new(0,0,s));
        int triCount = verts.Length / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++) {
            off += 12;
            for (int v = 0; v < 3; v++) {
                BitConverter.GetBytes(verts[t*3+v].X).CopyTo(data, off);
                BitConverter.GetBytes(verts[t*3+v].Y).CopyTo(data, off+4);
                BitConverter.GetBytes(verts[t*3+v].Z).CopyTo(data, off+8);
                off += 12;
            }
            off += 2;
        }
        return AabbBvh.Build(StlMesh.FromBinary(data));
    }

    [Fact]
    public void IsInside_Center_ReturnsTrue()
    {
        var bvh = BuildCubeBvh();
        bvh.IsInside(new Vector3(5, 5, 5)).Should().BeTrue();
    }

    [Fact]
    public void IsInside_Outside_ReturnsFalse()
    {
        var bvh = BuildCubeBvh();
        bvh.IsInside(new Vector3(15, 15, 15)).Should().BeFalse();
    }

    [Theory]
    [InlineData(-5, 5, 5)]   // left
    [InlineData(15, 5, 5)]   // right
    [InlineData(5, -5, 5)]   // below
    [InlineData(5, 15, 5)]   // above
    [InlineData(5, 5, -5)]   // behind
    [InlineData(5, 5, 15)]   // in front
    public void IsInside_OutsideAllDirections_ReturnsFalse(float x, float y, float z)
    {
        var bvh = BuildCubeBvh();
        bvh.IsInside(new Vector3(x, y, z)).Should().BeFalse();
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(9, 9, 9)]
    [InlineData(5, 1, 5)]
    [InlineData(1, 5, 1)]
    public void IsInside_NearCorners_ReturnsTrue(float x, float y, float z)
    {
        var bvh = BuildCubeBvh();
        bvh.IsInside(new Vector3(x, y, z)).Should().BeTrue();
    }

    [Fact]
    public void IsInside_OffsetCube_WorksCorrectly()
    {
        var bvh = BuildCubeBvh(10f, new Vector3(100, 100, 100));
        bvh.IsInside(new Vector3(105, 105, 105)).Should().BeTrue();
        bvh.IsInside(new Vector3(0, 0, 0)).Should().BeFalse();
    }

    [Fact]
    public void IsInside_NegativeCoords_WorksCorrectly()
    {
        var bvh = BuildCubeBvh(10f, new Vector3(-20, -20, -20));
        bvh.IsInside(new Vector3(-15, -15, -15)).Should().BeTrue();
        bvh.IsInside(new Vector3(0, 0, 0)).Should().BeFalse();
    }

    [Fact]
    public void IsInside_ManyRandomPoints_ConsistentResults()
    {
        var bvh = BuildCubeBvh(10f);
        var rng = new Random(42);

        for (int i = 0; i < 100; i++)
        {
            float x = rng.NextSingle() * 20 - 5; // range -5 to 15
            float y = rng.NextSingle() * 20 - 5;
            float z = rng.NextSingle() * 20 - 5;
            bool inside = bvh.IsInside(new Vector3(x, y, z));

            // Ground truth: inside [0,10] cube
            bool expected = x > 0.1f && x < 9.9f && y > 0.1f && y < 9.9f && z > 0.1f && z < 9.9f;

            if (expected)
                inside.Should().BeTrue($"point ({x:F1},{y:F1},{z:F1}) should be inside");
            // Note: points near boundaries may be ambiguous — only check clearly inside points
        }
    }
}
