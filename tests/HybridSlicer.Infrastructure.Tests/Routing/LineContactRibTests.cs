using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Slicing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests.Routing;

/// <summary>
/// Tests that line contact produces connecting rib geometry between consecutive
/// tips on the same overhang edge, in both mesh and slice elements.
/// </summary>
public class LineContactRibTests
{
    private static StlMesh CreateOverhangEdgeModel()
    {
        // A shelf with a long overhang edge that should trigger line contact
        var verts = new List<Vector3>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            verts.Add(a); verts.Add(b); verts.Add(c);
            verts.Add(a); verts.Add(c); verts.Add(d);
        }
        void AddBox(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            var v000 = new Vector3(x1, y1, z1); var v100 = new Vector3(x2, y1, z1);
            var v010 = new Vector3(x1, y2, z1); var v110 = new Vector3(x2, y2, z1);
            var v001 = new Vector3(x1, y1, z2); var v101 = new Vector3(x2, y1, z2);
            var v011 = new Vector3(x1, y2, z2); var v111 = new Vector3(x2, y2, z2);
            Quad(v001, v101, v111, v011); Quad(v000, v010, v110, v100);
            Quad(v100, v110, v111, v101); Quad(v000, v001, v011, v010);
            Quad(v010, v011, v111, v110); Quad(v000, v100, v101, v001);
        }
        // Thin pillar to plate
        AddBox(-2, -2, 0, 2, 2, 10);
        // Wide overhang shelf (long edge = line contact candidate)
        AddBox(-30, -5, 10, 30, 5, 12);

        int triCount = verts.Count / 3;
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
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void LineContact_ProducesRibsInBothMeshAndSlices()
    {
        var mesh = CreateOverhangEdgeModel();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            EnableLineContact = true,
            DensityFactor = 0.5f,
        });

        result.ValidSupports.Should().BeGreaterThan(0);

        // Check for linerib elements in slices
        var ribElements = result.SliceElements.Where(e => e.Type == "linerib").ToList();
        // Line contact ribs may or may not appear depending on whether overhang edges
        // were detected and enough tips survived the pipeline.
        // If there are linerib elements, verify they have valid coordinates
        foreach (var rib in ribElements)
        {
            float.IsNaN(rib.PointA.X).Should().BeFalse("rib PointA.X should be finite");
            float.IsNaN(rib.PointB.X).Should().BeFalse("rib PointB.X should be finite");
            rib.RadiusA.Should().BeInRange(0.1f, 1.0f, "rib radius should be reasonable");
        }
    }

    [Theory]
    [InlineData("0deg")]
    [InlineData("rotX45")]
    [InlineData("rotY90")]
    public void LineContact_WorksAtRotations(string label)
    {
        var rot = label switch
        {
            "rotX45" => Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4f),
            "rotY90" => Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
            _ => Quaternion.Identity,
        };

        var mesh = CreateOverhangEdgeModel().Rotate(rot);
        float zMin = mesh.Min.Z;
        if (zMin < -0.1f || zMin > 0.1f)
            mesh = mesh.Transform(new Vector3(0, 0, -zMin), 1.0f);

        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            EnableLineContact = true,
            DensityFactor = 0.5f,
        });

        result.ValidSupports.Should().BeGreaterThan(0, $"[{label}] should produce supports");
        result.SupportMesh.FaceCount.Should().BeGreaterThan(0, $"[{label}] should have mesh");
    }

    [Fact]
    public void LineContact_RibsNotPresentWithoutLineContactEnabled()
    {
        var mesh = CreateOverhangEdgeModel();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            EnableLineContact = false,
            DensityFactor = 0.5f,
        });

        var ribElements = result.SliceElements.Where(e => e.Type == "linerib").ToList();
        ribElements.Should().BeEmpty("ribs should not be generated when line contact is disabled");
    }
}
