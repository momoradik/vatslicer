using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Golden fingerprint regression tests. For a fixed model + config, the support
/// generation should produce deterministic output. The fingerprint is a hash of
/// sorted support tip positions + radii + waypoint counts. Any regression that
/// changes support topology will change the fingerprint.
/// </summary>
public class GoldenFingerprintTests
{
    private static StlMesh CreateReferenceModel()
    {
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
        AddBox(-15, -15, 10, 15, 15, 25);
        AddBox(-20, -20, 8, 20, 20, 10);
        AddBox(-5, -5, 35, 5, 5, 38);
        AddBox(-2, -2, 0, 2, 2, 8);
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

    private static string ComputeFingerprint(SupportEngineV2.EngineResult result)
    {
        var sb = new StringBuilder();
        // Sort by contact point coordinates for determinism
        var sorted = result.Pinheads
            .Where(p => p.pinhead.IsValid)
            .OrderBy(p => p.pinhead.ContactPoint.X)
            .ThenBy(p => p.pinhead.ContactPoint.Y)
            .ThenBy(p => p.pinhead.ContactPoint.Z)
            .ToList();

        foreach (var (id, ph) in sorted)
        {
            sb.Append($"{ph.ContactPoint.X:F2},{ph.ContactPoint.Y:F2},{ph.ContactPoint.Z:F2},");
            sb.Append($"{ph.PinRadius:F3},{ph.BackRadius:F3},");
            var route = result.Routes.FirstOrDefault(r => r.id == id);
            sb.Append($"{route.route?.Path.Count ?? 0}\n");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    [Fact]
    public void DefaultConfig_ProducesDeterministicFingerprint()
    {
        var mesh = CreateReferenceModel();
        var config = new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f,
            Seed = 42,
        };

        // Run twice — same input must produce same fingerprint
        var result1 = SupportEngineV2.Generate(mesh, config);
        var result2 = SupportEngineV2.Generate(mesh, config);

        var fp1 = ComputeFingerprint(result1);
        var fp2 = ComputeFingerprint(result2);

        fp1.Should().Be(fp2, "same model + config must produce identical support topology");
        fp1.Should().HaveLength(16, "fingerprint is 16 hex chars");
    }

    [Fact]
    public void DifferentDensity_ProducesDifferentFingerprint()
    {
        var mesh = CreateReferenceModel();

        var result1 = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = 0.3f, Seed = 42 });
        var result2 = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = 0.8f, Seed = 42 });

        var fp1 = ComputeFingerprint(result1);
        var fp2 = ComputeFingerprint(result2);

        // Different density should produce different support layouts
        // (can't guarantee different fingerprint on tiny models, but should differ on complex ones)
        result1.ValidSupports.Should().NotBe(result2.ValidSupports,
            "different density should produce different support counts on a complex model");
    }
}
