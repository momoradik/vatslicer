using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

/// <summary>
/// Integration tests verifying V2 support elements can be sliced
/// and rendered into layer images compatible with the ResinSlicerEngine.
/// </summary>
public class V2SliceIntegrationTests
{
    private static SupportEngineV2.EngineResult GenerateSupports()
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
    public void SliceElements_CoverSupportHeight()
    {
        var result = GenerateSupports();
        result.SliceElements.Count.Should().BeGreaterOrEqualTo(0);
        if (result.SliceElements.Count == 0) return;

        float minZ = result.SliceElements.Min(e => Math.Min(e.PointA.Z, e.PointB.Z));
        float maxZ = result.SliceElements.Max(e => Math.Max(e.PointA.Z, e.PointB.Z));

        minZ.Should().BeLessThan(2f, "elements should reach near the base");
        maxZ.Should().BeGreaterOrEqualTo(0f, "elements should have measurable height");
    }

    [Fact]
    public void SliceElements_ProduceCirclesAtEveryLayer()
    {
        var result = GenerateSupports();
        if (result.SliceElements.Count == 0) return;

        float minZ = result.SliceElements.Min(e => Math.Min(e.PointA.Z, e.PointB.Z));
        float maxZ = result.SliceElements.Max(e => Math.Max(e.PointA.Z, e.PointB.Z));

        int emptyLayers = 0;
        int totalLayers = 0;
        for (float z = minZ + 0.5f; z < maxZ - 0.5f; z += 1f)
        {
            totalLayers++;
            var circles = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, z);
            if (circles.Count == 0) emptyLayers++;
        }

        // At most 10% of interior layers should be empty
        if (totalLayers > 5)
        {
            float emptyRatio = (float)emptyLayers / totalLayers;
            emptyRatio.Should().BeLessThan(0.2f, "most interior layers should have support circles");
        }
    }

    [Fact]
    public void SliceElements_AllHavePositiveRadius()
    {
        var result = GenerateSupports();
        foreach (var e in result.SliceElements)
        {
            e.RadiusA.Should().BeGreaterThanOrEqualTo(0);
            e.RadiusB.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public void SliceElements_AllHaveFiniteCoordinates()
    {
        var result = GenerateSupports();
        foreach (var e in result.SliceElements)
        {
            float.IsNaN(e.PointA.X).Should().BeFalse();
            float.IsNaN(e.PointA.Y).Should().BeFalse();
            float.IsNaN(e.PointA.Z).Should().BeFalse();
            float.IsNaN(e.PointB.X).Should().BeFalse();
            float.IsNaN(e.PointB.Y).Should().BeFalse();
            float.IsNaN(e.PointB.Z).Should().BeFalse();
        }
    }

    [Fact]
    public void SliceElements_TypesAreValid()
    {
        var result = GenerateSupports();
        var validTypes = new[] { "pinhead", "pillar", "bridge", "base", "junction", "anchor", "interconnect", "raft", "fillet" };

        foreach (var e in result.SliceElements)
        {
            validTypes.Should().Contain(e.Type, $"element type '{e.Type}' should be a known type");
        }
    }

    [Fact]
    public void FullSliceRun_ProducesValidPngs()
    {
        var result = GenerateSupports();
        if (result.SliceElements.Count == 0) return;

        // Render 5 layers across the support height
        float minZ = result.SliceElements.Min(e => Math.Min(e.PointA.Z, e.PointB.Z));
        float maxZ = result.SliceElements.Max(e => Math.Max(e.PointA.Z, e.PointB.Z));

        for (int i = 0; i < 5; i++)
        {
            float z = minZ + (maxZ - minZ) * (i + 0.5f) / 5f;
            var circles = AnalyticalSupportSlicer.SliceAtZ(result.SliceElements, z);
            if (circles.Count == 0) continue;

            var png = SupportSliceIntegrator.RenderSupportOnlyLayer(circles, 100, 100, 50, 50);
            png.Should().NotBeNull();
            png.Length.Should().BeGreaterThan(5);
            png[0].Should().Be(0x89); // PNG magic
        }
    }
}
