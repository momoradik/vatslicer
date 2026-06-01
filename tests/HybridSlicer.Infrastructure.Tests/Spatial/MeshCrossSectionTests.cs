using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests.Spatial;

/// <summary>
/// Tests for the MeshCrossSectionEngine which underpins the BatchSlicer.
/// </summary>
public class MeshCrossSectionTests
{
    private static StlMesh CreateCube(float size = 10f)
    {
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
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
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void CrossSection_CubeMidHeight_ReturnsSquare()
    {
        var mesh = CreateCube(10f);
        var contours = MeshCrossSectionEngine.CrossSection(mesh, 5f);

        contours.Should().NotBeEmpty("cube mid-height should have contours");
        contours[0].Count.Should().BeGreaterThanOrEqualTo(4, "cross-section should be roughly square");
    }

    [Fact]
    public void CrossSection_BelowCube_ReturnsEmpty()
    {
        var mesh = CreateCube(10f);
        var contours = MeshCrossSectionEngine.CrossSection(mesh, -1f);
        contours.Should().BeEmpty();
    }

    [Fact]
    public void CrossSection_AboveCube_ReturnsEmpty()
    {
        var mesh = CreateCube(10f);
        var contours = MeshCrossSectionEngine.CrossSection(mesh, 11f);
        contours.Should().BeEmpty();
    }

    [Fact]
    public void CrossSection_MultipleHeights_ConsistentContours()
    {
        var mesh = CreateCube(10f);

        for (float z = 1f; z < 10f; z += 2f)
        {
            var contours = MeshCrossSectionEngine.CrossSection(mesh, z);
            contours.Should().NotBeEmpty($"height {z} should have contours");
        }
    }

    [Fact]
    public void CrossSection_ContourPointsAreFinite()
    {
        var mesh = CreateCube(10f);
        var contours = MeshCrossSectionEngine.CrossSection(mesh, 5f);

        foreach (var contour in contours)
        foreach (var pt in contour)
        {
            float.IsNaN(pt.X).Should().BeFalse();
            float.IsNaN(pt.Y).Should().BeFalse();
        }
    }

    [Fact]
    public void CrossSection_ContourBoundsMatchCube()
    {
        var mesh = CreateCube(10f);
        var contours = MeshCrossSectionEngine.CrossSection(mesh, 5f);

        if (contours.Count > 0)
        {
            float minX = contours.SelectMany(c => c).Min(p => p.X);
            float maxX = contours.SelectMany(c => c).Max(p => p.X);
            float minY = contours.SelectMany(c => c).Min(p => p.Y);
            float maxY = contours.SelectMany(c => c).Max(p => p.Y);

            // Cross-section of 10mm cube should span roughly 0-10mm in X and Y
            minX.Should().BeApproximately(0f, 0.5f);
            maxX.Should().BeApproximately(10f, 0.5f);
            minY.Should().BeApproximately(0f, 0.5f);
            maxY.Should().BeApproximately(10f, 0.5f);
        }
    }
}
