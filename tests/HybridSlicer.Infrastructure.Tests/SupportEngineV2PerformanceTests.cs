using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Performance tests for the complete V2 engine pipeline.
/// </summary>
public class SupportEngineV2PerformanceTests
{
    private static StlMesh CreateCube(float size, float z)
    {
        var o = new Vector3(-size/2, -size/2, z);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = size;
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
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void V2_SmallCube_Under500ms()
    {
        var mesh = CreateCube(10f, 5f);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(500);
    }

    [Fact]
    public void V2_MediumCube_Under1s()
    {
        var mesh = CreateCube(50f, 25f);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [Fact]
    public void V2_LargeCube_Under3s()
    {
        var mesh = CreateCube(100f, 50f);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(3000);
    }

    [Fact]
    public void V2_5Runs_ConsistentTiming()
    {
        var mesh = CreateCube(20f, 10f);
        var times = new List<long>();

        for (int i = 0; i < 5; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { Seed = 42 });
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);
        }

        var max = times.Max();
        var min = times.Where(t => t > 0).DefaultIfEmpty(1).Min();
        if (min > 0)
            ((float)max / min).Should().BeLessThan(10f);
    }

    [Fact]
    public void V2_RealModel_Under2s()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6; i++) dir = Path.GetDirectoryName(dir)!;
        var path = Path.Combine(dir, "test_slice", "floating_model.stl");
        if (!File.Exists(path)) return;

        var data = File.ReadAllBytes(path);
        var (mesh, _) = MeshValidator.ValidateAndRepair(data);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
        result.ValidSupports.Should().BeGreaterThan(0);
    }

    [Fact]
    public void V2_StlExport_Under100ms()
    {
        var mesh = CreateCube(20f, 10f);
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        result.SupportMesh.ToStlBinary();
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100);
    }
}
