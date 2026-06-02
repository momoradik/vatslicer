using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Routing;
using HybridSlicer.Infrastructure.Resin.Spatial;
using HybridSlicer.Infrastructure.Resin.Validation;

namespace HybridSlicer.Infrastructure.Tests.Validation;

public class CollisionValidatorPerformanceTests
{
    [Fact]
    public void ValidateAll_50Supports_Under2Seconds()
    {
        // Build a simple BVH
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
            verts[vi++]=a; verts[vi++]=b; verts[vi++]=c;
            verts[vi++]=a; verts[vi++]=c; verts[vi++]=d;
        }
        float s = 10f;
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
        var bvh = AabbBvh.Build(StlMesh.FromBinary(data));

        // Create 50 pinheads outside the cube
        var pinheads = new List<(string id, PinheadOptimizer.Pinhead pinhead)>();
        for (int i = 0; i < 50; i++)
        {
            pinheads.Add(($"s{i}", new PinheadOptimizer.Pinhead
            {
                ContactPoint = new(15 + i, 5, 15), Direction = new(0, 0, -1),
                PinCenter = new(15 + i, 5, 14.8f), BackCenter = new(15 + i, 5, 14.3f),
                JunctionPoint = new(15 + i, 5, 13.8f),
                PinRadius = 0.2f, BackRadius = 0.5f, Width = 1f,
                Clearance = 10f, IsValid = true, NeedsAnchor = false,
            }));
        }

        var routes = pinheads.Select(p => (p.id, new PillarRouter.PillarRoute
        {
            Path = new()
            {
                new() { Position = new(15 + pinheads.IndexOf(p), 5, 13.8f), Radius = 0.5f, Type = "junction" },
                new() { Position = new(15 + pinheads.IndexOf(p), 5, 0), Radius = 1f, Type = "base" },
            },
            ReachesGround = true, TotalLength = 13.8f,
        })).ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = CollisionValidator.ValidateAll(pinheads, routes, new(), bvh);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "50 supports should validate in under 2s");
        result.TotalSupportsChecked.Should().Be(50);
    }
}
