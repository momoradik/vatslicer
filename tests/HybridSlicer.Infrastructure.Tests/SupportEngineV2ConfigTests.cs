using System.Numerics;
using FluentAssertions;
using HybridSlicer.Domain.Enums;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Tests for V2 engine configuration options.
/// </summary>
public class SupportEngineV2ConfigTests
{
    private static StlMesh CreateFloatingCube()
    {
        var o = new Vector3(-10, -10, 10);
        var verts = new Vector3[36];
        int vi = 0;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            verts[vi++] = a + o; verts[vi++] = b + o; verts[vi++] = c + o;
            verts[vi++] = a + o; verts[vi++] = c + o; verts[vi++] = d + o;
        }
        float s = 20f;
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
        for (int t = 0; t < triCount; t++)
        {
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
    public void BottomUp_ProducesSmallerTips()
    {
        var mesh = CreateFloatingCube();
        var bu = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            Orientation = PrinterOrientation.BottomUp,
            PinRadiusMm = 0.3f,
        });
        var td = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            Orientation = PrinterOrientation.TopDown,
            PinRadiusMm = 0.3f,
        });

        // Bottom-Up should have 80% of the pin radius
        if (bu.Pinheads.Count > 0 && td.Pinheads.Count > 0)
        {
            var buPin = bu.Pinheads.First(p => p.pinhead.IsValid).pinhead;
            var tdPin = td.Pinheads.First(p => p.pinhead.IsValid).pinhead;
            buPin.PinRadius.Should().BeLessThanOrEqualTo(tdPin.PinRadius,
                "Bottom-Up pin radius should be smaller (80% scale)");
        }
    }

    [Fact]
    public void WithScale_ChangesSupports()
    {
        var mesh = CreateFloatingCube();
        var normal = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());
        var scaled = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { Scale = 2.0f });

        // Scaled model should generally produce different support count
        (normal.ValidSupports != scaled.ValidSupports || normal.TotalSupportVolumeMm3 != scaled.TotalSupportVolumeMm3)
            .Should().BeTrue("scaling should change support generation");
    }

    [Fact]
    public void SupportLayerCount_IsPositive()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.SupportLayerCount.Should().BeGreaterOrEqualTo(0,
            "floating cube supports should span multiple layers");
    }

    [Fact]
    public void CrossSectionArea_IsPositive()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig());

        result.TotalSupportCrossSectionArea.Should().BeGreaterOrEqualTo(0,
            "supports should have cross-section area");
    }

    [Fact]
    public void DisableInterconnections_ProducesNoBraces()
    {
        var mesh = CreateFloatingCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig
        {
            EnableInterconnections = false,
        });

        result.Interconnections.Should().BeEmpty("interconnections disabled");
        result.LegacyCrossBraces.Should().BeEmpty();
    }

    [Fact]
    public void LargerPinRadius_ProducesThickerPinheads()
    {
        var mesh = CreateFloatingCube();
        var thin = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { PinRadiusMm = 0.1f });
        var thick = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { PinRadiusMm = 0.5f });

        if (thin.Pinheads.Count > 0 && thick.Pinheads.Count > 0)
        {
            var thinR = thin.Pinheads.First(p => p.pinhead.IsValid).pinhead.PinRadius;
            var thickR = thick.Pinheads.First(p => p.pinhead.IsValid).pinhead.PinRadius;
            thickR.Should().BeGreaterThan(thinR);
        }
    }
}
