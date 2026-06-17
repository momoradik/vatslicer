using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class SuctionCupDetectorTests
{
    private static StlMesh BuildMesh(List<Vector3> verts)
    {
        int triCount = verts.Count / 3;
        var data = new byte[84 + triCount * 50];
        BitConverter.GetBytes((uint)triCount).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < triCount; t++)
        {
            var v0 = verts[t * 3]; var v1 = verts[t * 3 + 1]; var v2 = verts[t * 3 + 2];
            var n = Vector3.Cross(v1 - v0, v2 - v0);
            float len = n.Length(); if (len > 1e-6f) n /= len; else n = Vector3.UnitZ;
            BitConverter.GetBytes(n.X).CopyTo(data, off); BitConverter.GetBytes(n.Y).CopyTo(data, off + 4);
            BitConverter.GetBytes(n.Z).CopyTo(data, off + 8); off += 12;
            for (int v = 0; v < 3; v++)
            { BitConverter.GetBytes(verts[t*3+v].X).CopyTo(data,off); BitConverter.GetBytes(verts[t*3+v].Y).CopyTo(data,off+4);
              BitConverter.GetBytes(verts[t*3+v].Z).CopyTo(data,off+8); off+=12; }
            off += 2;
        }
        return StlMesh.FromBinary(data);
    }

    private static void AddBox(List<Vector3> v, float x1,float y1,float z1,float x2,float y2,float z2)
    {
        void Q(Vector3 a,Vector3 b,Vector3 c,Vector3 d){v.Add(a);v.Add(b);v.Add(c);v.Add(a);v.Add(c);v.Add(d);}
        var v000=new Vector3(x1,y1,z1);var v100=new Vector3(x2,y1,z1);var v010=new Vector3(x1,y2,z1);
        var v110=new Vector3(x2,y2,z1);var v001=new Vector3(x1,y1,z2);var v101=new Vector3(x2,y1,z2);
        var v011=new Vector3(x1,y2,z2);var v111=new Vector3(x2,y2,z2);
        Q(v001,v101,v111,v011);Q(v000,v010,v110,v100);Q(v100,v110,v111,v101);
        Q(v000,v001,v011,v010);Q(v010,v011,v111,v110);Q(v000,v100,v101,v001);
    }

    [Fact]
    public void SolidBox_NoSuctionWarnings()
    {
        var verts = new List<Vector3>();
        AddBox(verts, -10, -10, 0, 10, 10, 20);
        var mesh = BuildMesh(verts);
        var warnings = SuctionCupDetector.Detect(mesh);
        warnings.Should().BeEmpty("solid box has no suction pockets");
    }

    [Fact]
    public void Detect_NoThrow_OnEmptyishMesh()
    {
        var verts = new List<Vector3>();
        AddBox(verts, 0, 0, 0, 1, 1, 1);
        var mesh = BuildMesh(verts);
        var warnings = SuctionCupDetector.Detect(mesh);
        warnings.Should().NotBeNull();
    }

    [Fact]
    public void Warnings_HaveValidFields()
    {
        var verts = new List<Vector3>();
        // Narrow pillar then wide shelf = area jump
        AddBox(verts, -2, -2, 0, 2, 2, 10);
        AddBox(verts, -20, -20, 10, 20, 20, 15);
        var mesh = BuildMesh(verts);
        var warnings = SuctionCupDetector.Detect(mesh, new SuctionCupDetector.DetectionConfig
        {
            LayerHeightMm = 1f, MinAreaRatio = 1.3f, MinPocketDepthMm = 1f,
        });
        foreach (var w in warnings)
        {
            w.Severity.Should().BeOneOf("low", "medium", "high");
            w.DepthMm.Should().BeGreaterThan(0);
            w.Description.Should().NotBeNullOrEmpty();
        }
    }
}
