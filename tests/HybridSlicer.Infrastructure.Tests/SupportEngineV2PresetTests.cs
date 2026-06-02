using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests verifying different preset configurations produce different results.
/// Maps to the frontend dropdown: light/medium/heavy.
/// </summary>
public class SupportEngineV2PresetTests
{
    private static StlMesh CreateCube()
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
        return StlMesh.FromBinary(data);
    }

    [Fact]
    public void Light_HasSmallerPinRadius()
    {
        var mesh = CreateCube();
        var light = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { PinRadiusMm = 0.1f });
        var heavy = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { PinRadiusMm = 0.4f });

        if (light.Pinheads.Any(p => p.pinhead.IsValid) && heavy.Pinheads.Any(p => p.pinhead.IsValid))
        {
            var lr = light.Pinheads.First(p => p.pinhead.IsValid).pinhead.PinRadius;
            var hr = heavy.Pinheads.First(p => p.pinhead.IsValid).pinhead.PinRadius;
            lr.Should().BeLessThan(hr);
        }
    }

    [Fact]
    public void Heavy_HasLargerBase()
    {
        var mesh = CreateCube();
        var light = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { BaseRadiusMm = 1.2f });
        var heavy = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { BaseRadiusMm = 3.0f });

        if (light.Routes.Count > 0 && heavy.Routes.Count > 0)
        {
            var lBase = light.Routes.Max(r => r.route.Path.Where(w => w.Type == "base").Max(w => w.Radius));
            var hBase = heavy.Routes.Max(r => r.route.Path.Where(w => w.Type == "base").Max(w => w.Radius));
            hBase.Should().BeGreaterThan(lBase);
        }
    }

    [Fact]
    public void Heavy_UsesMoreMaterial()
    {
        var mesh = CreateCube();
        var light = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            PinRadiusMm = 0.1f, BackRadiusMm = 0.3f, PillarRadiusMm = 0.3f, BaseRadiusMm = 1.2f,
        });
        var heavy = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            PinRadiusMm = 0.4f, BackRadiusMm = 0.75f, PillarRadiusMm = 0.75f, BaseRadiusMm = 3.0f,
        });

        heavy.TotalSupportVolumeMm3.Should().BeGreaterThan(light.TotalSupportVolumeMm3);
    }

    [Fact]
    public void AllPresets_ProduceValidOutput()
    {
        var mesh = CreateCube();
        var configs = new[]
        {
            new SupportEngineV2.EngineConfig { PinRadiusMm = 0.1f, BackRadiusMm = 0.3f },  // light
            new SupportEngineV2.EngineConfig { PinRadiusMm = 0.2f, BackRadiusMm = 0.5f },  // medium
            new SupportEngineV2.EngineConfig { PinRadiusMm = 0.4f, BackRadiusMm = 0.75f }, // heavy
            new SupportEngineV2.EngineConfig { PinRadiusMm = 0.05f, BackRadiusMm = 0.2f }, // needle
            new SupportEngineV2.EngineConfig { PinRadiusMm = 0.3f, BackRadiusMm = 0.5f },  // mushroom
        };

        foreach (var cfg in configs)
        {
            var result = SupportEngineV2.Generate(mesh, cfg);
            result.Should().NotBeNull();
            result.ValidSupports.Should().BeGreaterThan(0);
            result.SupportMesh.FaceCount.Should().BeGreaterThan(0);
        }
    }
}
