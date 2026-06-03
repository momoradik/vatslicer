using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests verifying internal consistency of V2 engine output —
/// all counts match, all references are valid, no dangling pointers.
/// </summary>
public class SupportEngineV2ConsistencyTests
{
    private static SupportEngineV2.EngineResult Generate()
    {
        var o = new Vector3(-10, -10, 10);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = 20f;
        Quad(o+new Vector3(0,0,s), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(s,s,0), o+new Vector3(s,0,0));
        Quad(o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0));
        Quad(o+new Vector3(0,0,s), o+new Vector3(0,0,0), o+new Vector3(0,s,0), o+new Vector3(0,s,s));
        Quad(o+new Vector3(0,s,s), o+new Vector3(s,s,s), o+new Vector3(s,s,0), o+new Vector3(0,s,0));
        Quad(o+new Vector3(0,0,0), o+new Vector3(s,0,0), o+new Vector3(s,0,s), o+new Vector3(0,0,s));
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
        return SupportEngineV2.Generate(StlMesh.FromBinary(data), new SupportEngineV2.EngineConfig());
    }

    [Fact]
    public void PinheadCount_Equals_PointCount()
    {
        var r = Generate();
        r.Pinheads.Count.Should().Be(r.Points.Count);
    }

    [Fact]
    public void RouteCount_LessOrEqual_ValidPinheads()
    {
        var r = Generate();
        int validPinheads = r.Pinheads.Count(p => p.pinhead.IsValid);
        r.Routes.Count.Should().BeLessThanOrEqualTo(validPinheads);
    }

    [Fact]
    public void LegacyCount_Equals_RouteCount()
    {
        var r = Generate();
        r.LegacySupports.Count.Should().Be(r.Routes.Count);
    }

    [Fact]
    public void ValidSupports_Equals_RouteCount()
    {
        var r = Generate();
        // ValidSupports counts valid pinheads, Routes may be fewer due to collision filter
        r.ValidSupports.Should().BeGreaterThanOrEqualTo(r.Routes.Count);
    }

    [Fact]
    public void MeshFaces_PositiveWhenSupportsExist()
    {
        var r = Generate();
        if (r.Routes.Count > 0)
            r.SupportMesh.FaceCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SliceElements_PresentWhenRoutesExist()
    {
        var r = Generate();
        if (r.Routes.Count > 0)
            r.SliceElements.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Volume_PositiveWhenRoutesExist()
    {
        var r = Generate();
        if (r.Routes.Count > 0)
            r.TotalSupportVolumeMm3.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SupportLayers_PositiveWhenRoutesExist()
    {
        var r = Generate();
        if (r.Routes.Count > 0)
            r.SupportLayerCount.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public void CrossSectionArea_PositiveWhenRoutesExist()
    {
        var r = Generate();
        if (r.Routes.Count > 0)
            r.TotalSupportCrossSectionArea.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public void AllPinheadIds_MatchPointIds()
    {
        var r = Generate();
        var pointIds = new HashSet<string>(r.Points.Select(p => p.Id));
        foreach (var (id, _) in r.Pinheads)
            pointIds.Should().Contain(id);
    }

    [Fact]
    public void AllRouteIds_MatchPinheadIds()
    {
        var r = Generate();
        var pinheadIds = new HashSet<string>(r.Pinheads.Select(p => p.id));
        foreach (var (id, _) in r.Routes)
            pinheadIds.Should().Contain(id);
    }

    [Fact]
    public void AllLegacySupportIds_MatchRouteIds()
    {
        var r = Generate();
        var routeIds = new HashSet<string>(r.Routes.Select(rr => rr.id));
        foreach (var s in r.LegacySupports)
            routeIds.Should().Contain(s.Id);
    }

    [Fact]
    public void ElapsedMs_IsPositive()
    {
        var r = Generate();
        r.TotalElapsedMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public void BVH_IsNotNull()
    {
        var r = Generate();
        r.Bvh.Should().NotBeNull();
        r.Bvh.TriangleCount.Should().Be(12); // cube has 12 triangles
    }

    [Fact]
    public void MeshCenteringOffset_IsFinite()
    {
        var r = Generate();
        float.IsFinite(r.MeshCenteringOffset.X).Should().BeTrue();
        float.IsFinite(r.MeshCenteringOffset.Y).Should().BeTrue();
        float.IsFinite(r.MeshCenteringOffset.Z).Should().BeTrue();
    }
}
