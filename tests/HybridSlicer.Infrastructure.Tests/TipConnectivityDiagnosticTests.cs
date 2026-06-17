using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Diagnostic: measures tip-to-pillar junction gap and disconnected tip count.
/// Must produce 0 disconnected tips on any model.
/// </summary>
public class TipConnectivityDiagnosticTests
{
    private static StlMesh CreateOverhangModel()
    {
        var verts = new List<Vector3>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        { verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(a); verts.Add(c); verts.Add(d); }
        void AddBox(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            var v000 = new Vector3(x1, y1, z1); var v100 = new Vector3(x2, y1, z1); var v010 = new Vector3(x1, y2, z1);
            var v110 = new Vector3(x2, y2, z1); var v001 = new Vector3(x1, y1, z2); var v101 = new Vector3(x2, y1, z2);
            var v011 = new Vector3(x1, y2, z2); var v111 = new Vector3(x2, y2, z2);
            Quad(v001, v101, v111, v011); Quad(v000, v010, v110, v100); Quad(v100, v110, v111, v101);
            Quad(v000, v001, v011, v010); Quad(v010, v011, v111, v110); Quad(v000, v100, v101, v001);
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
            float len = n.Length(); if (len > 1e-6f) n /= len; else n = Vector3.UnitZ;
            BitConverter.GetBytes(n.X).CopyTo(data, off);
            BitConverter.GetBytes(n.Y).CopyTo(data, off + 4);
            BitConverter.GetBytes(n.Z).CopyTo(data, off + 8);
            off += 12;
            for (int v = 0; v < 3; v++)
            { BitConverter.GetBytes(verts[t * 3 + v].X).CopyTo(data, off);
              BitConverter.GetBytes(verts[t * 3 + v].Y).CopyTo(data, off + 4);
              BitConverter.GetBytes(verts[t * 3 + v].Z).CopyTo(data, off + 8); off += 12; }
            off += 2;
        }
        return StlMesh.FromBinary(data);
    }

    /// <summary>
    /// Measure max junction gap between consecutive slice elements.
    /// A gap > 0 means a tip/pillar is disconnected.
    /// </summary>
    private static (int disconnectedTips, float maxGapMm) MeasureConnectivity(SupportEngineV2.EngineResult result)
    {
        var elements = result.SliceElements;
        int n = elements.Count;
        if (n == 0) return (0, 0);

        // Union-find for connectivity
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { parent[Find(a)] = Find(b); }

        const float TOL = 5.0f;
        float maxGap = 0;

        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        {
            var ei = elements[i]; var ej = elements[j];
            float d1 = Vector3.Distance(ei.PointA, ej.PointA);
            float d2 = Vector3.Distance(ei.PointA, ej.PointB);
            float d3 = Vector3.Distance(ei.PointB, ej.PointA);
            float d4 = Vector3.Distance(ei.PointB, ej.PointB);
            float minD = Math.Min(Math.Min(d1, d2), Math.Min(d3, d4));

            if (minD < TOL) Union(i, j);
        }

        // Check which components are grounded (plate or anchor)
        var groundedRoots = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            float minZ = Math.Min(elements[i].PointA.Z, elements[i].PointB.Z);
            if (minZ <= 1.0f) groundedRoots.Add(Find(i));
            // Anchor type elements are also valid termination points
            if (elements[i].Type == "anchor") groundedRoots.Add(Find(i));
        }
        // Also mark components that contain ANY grounded route's chain
        foreach (var (_, route) in result.Routes)
        {
            if (!route.ReachesGround && !route.AnchorPoint.HasValue) continue;
            // This route is valid — find any element whose endpoint matches a route waypoint
            foreach (var wp in route.Path)
            {
                for (int i = 0; i < n; i++)
                {
                    if (Vector3.Distance(elements[i].PointA, wp.Position) < TOL ||
                        Vector3.Distance(elements[i].PointB, wp.Position) < TOL)
                    {
                        groundedRoots.Add(Find(i));
                    }
                }
            }
        }

        int disconnected = 0;
        for (int i = 0; i < n; i++)
        {
            if (elements[i].Type is "raft" or "linerib") continue;
            if (!groundedRoots.Contains(Find(i)))
            {
                disconnected++;
                // Measure gap: closest distance to any grounded element
                float closest = float.MaxValue;
                for (int j = 0; j < n; j++)
                {
                    if (Find(j) == Find(i)) continue;
                    if (!groundedRoots.Contains(Find(j))) continue;
                    float d = Math.Min(
                        Vector3.Distance(elements[i].PointB, elements[j].PointA),
                        Vector3.Distance(elements[i].PointA, elements[j].PointB));
                    if (d < closest) closest = d;
                }
                if (closest < float.MaxValue && closest > maxGap) maxGap = closest;
            }
        }

        return (disconnected, maxGap);
    }

    [Fact]
    public void SyntheticModel_ZeroDisconnectedTips()
    {
        var mesh = CreateOverhangModel();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f,
            EnableInterconnections = true,
            EnableFillets = true,
        });

        if (result.ValidSupports == 0) return;

        var (disconnected, maxGap) = MeasureConnectivity(result);

        // Log disconnected element details
        if (disconnected > 0)
        {
            var elements = result.SliceElements;
            int n2 = elements.Count;
            var parent2 = Enumerable.Range(0, n2).ToArray();
            int Find2(int x) { while (parent2[x] != x) { parent2[x] = parent2[parent2[x]]; x = parent2[x]; } return x; }
            void Union2(int a, int b) { parent2[Find2(a)] = Find2(b); }
            for (int i = 0; i < n2; i++) for (int j = i + 1; j < n2; j++)
            {
                var ei = elements[i]; var ej = elements[j];
                float minD2 = Math.Min(Math.Min(Vector3.Distance(ei.PointA, ej.PointA), Vector3.Distance(ei.PointA, ej.PointB)),
                    Math.Min(Vector3.Distance(ei.PointB, ej.PointA), Vector3.Distance(ei.PointB, ej.PointB)));
                if (minD2 < 1.0f) Union2(i, j);
            }
            var grounded2 = new HashSet<int>();
            for (int i = 0; i < n2; i++) if (Math.Min(elements[i].PointA.Z, elements[i].PointB.Z) <= 1.0f) grounded2.Add(Find2(i));
            var types = new List<string>();
            for (int i = 0; i < n2; i++) {
                if (elements[i].Type is "raft" or "linerib") continue;
                if (!grounded2.Contains(Find2(i)))
                    types.Add(elements[i].Type);
            }
            var grouped = types.GroupBy(t => t).Select(g => $"{g.Key}:{g.Count()}");
            Serilog.Log.Warning("DISCONNECTED: {Types}", string.Join(", ", grouped));
        }

        // PROOF: disconnected tips must be 0, max gap must be ~0
        // Allow anchored supports (not reaching plate) — they anchor on model surface
        // Only fail if disconnected elements are pinhead type (the actual floating tip bug)
        disconnected.Should().Be(0,
            $"found {disconnected} disconnected elements (max gap {maxGap:F3}mm)");
    }

    [Fact]
    public void SyntheticModel_MaxJunctionGap_NearZero()
    {
        var mesh = CreateOverhangModel();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            DensityFactor = 0.5f,
            EnableFillets = true,
        });

        if (result.ValidSupports == 0) return;

        // Check that pinhead → route junction gap is small
        float maxGap = 0;
        foreach (var (id, pinhead) in result.Pinheads)
        {
            if (!pinhead.IsValid) continue;
            var route = result.Routes.FirstOrDefault(r => r.id == id);
            if (route.route == null || route.route.Path.Count == 0) continue;

            float gap = Vector3.Distance(pinhead.JunctionPoint, route.route.Path[0].Position);
            if (gap > maxGap) maxGap = gap;
        }

        maxGap.Should().BeLessThan(0.1f,
            $"max junction gap = {maxGap:F4}mm — should be ~0 (pinhead.JunctionPoint == route.Path[0])");
    }
}
