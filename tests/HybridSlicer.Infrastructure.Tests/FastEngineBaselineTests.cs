using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Baseline measurements for the fast engine re-architecture.
/// Measures current engine timing and computes golden fingerprint on SINAa.stl.
/// </summary>
public class FastEngineBaselineTests
{
    private static StlMesh? LoadSINAa()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SINAa.stl");
        if (!File.Exists(path)) path = "SINAa.stl";
        if (!File.Exists(path)) return null;
        return StlMesh.FromBinary(File.ReadAllBytes(path));
    }

    private static string ComputeFingerprint(SupportEngineV2.EngineResult result)
    {
        var sb = new StringBuilder();
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

    private static StlMesh RotateMesh(StlMesh mesh, float rotXDeg, float rotYDeg, float rotZDeg)
    {
        if (rotXDeg == 0 && rotYDeg == 0 && rotZDeg == 0) return mesh;
        var rotX = Matrix4x4.CreateRotationX(rotXDeg * MathF.PI / 180f);
        var rotY = Matrix4x4.CreateRotationY(rotYDeg * MathF.PI / 180f);
        var rotZ = Matrix4x4.CreateRotationZ(rotZDeg * MathF.PI / 180f);
        var rot = rotX * rotY * rotZ;

        var newVerts = new Vector3[mesh.Vertices.Length];
        for (int i = 0; i < mesh.Vertices.Length; i++)
            newVerts[i] = Vector3.Transform(mesh.Vertices[i], rot);

        // Rebuild STL binary
        int tc = mesh.TriangleCount;
        var data = new byte[84 + tc * 50];
        BitConverter.GetBytes((uint)tc).CopyTo(data, 80);
        int off = 84;
        for (int t = 0; t < tc; t++)
        {
            var v0 = newVerts[t * 3]; var v1 = newVerts[t * 3 + 1]; var v2 = newVerts[t * 3 + 2];
            var n = Vector3.Cross(v1 - v0, v2 - v0);
            float len = n.Length(); if (len > 1e-6f) n /= len; else n = Vector3.UnitZ;
            BitConverter.GetBytes(n.X).CopyTo(data, off); BitConverter.GetBytes(n.Y).CopyTo(data, off + 4);
            BitConverter.GetBytes(n.Z).CopyTo(data, off + 8); off += 12;
            for (int v = 0; v < 3; v++)
            {
                BitConverter.GetBytes(newVerts[t * 3 + v].X).CopyTo(data, off);
                BitConverter.GetBytes(newVerts[t * 3 + v].Y).CopyTo(data, off + 4);
                BitConverter.GetBytes(newVerts[t * 3 + v].Z).CopyTo(data, off + 8);
                off += 12;
            }
            off += 2;
        }
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void Baseline_SINAa_Timing_And_Fingerprint()
    {
        var mesh = LoadSINAa();
        if (mesh == null) { Serilog.Log.Warning("SINAa.stl not found — skipping baseline"); return; }

        var rotations = new[]
        {
            ("rot0", 0f, 0f, 0f),
            ("rotX45", 45f, 0f, 0f),
            ("rotY90", 0f, 90f, 0f),
            ("rotX30Z60", 30f, 0f, 60f),
        };

        var config = new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f,
            EnableInterconnections = true,
            EnableFillets = true,
            Seed = 42,
        };

        foreach (var (name, rx, ry, rz) in rotations)
        {
            var rotated = RotateMesh(mesh, rx, ry, rz);
            var sw = Stopwatch.StartNew();
            var result = SupportEngineV2.Generate(rotated, config);
            sw.Stop();

            var fp = ComputeFingerprint(result);

            var msg = $"BASELINE {name}: {sw.ElapsedMilliseconds}ms, {result.ValidSupports} supports, " +
                $"{result.Interconnections.Count} braces, {result.SupportMesh.FaceCount} mesh faces, fp={fp}";
            Console.WriteLine(msg);
            System.Diagnostics.Trace.WriteLine(msg);

            result.ValidSupports.Should().BeGreaterThan(0, $"{name} should produce supports");
        }
    }
}
