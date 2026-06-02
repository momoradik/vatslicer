using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin;

namespace HybridSlicer.Infrastructure.Tests;

/// <summary>
/// Boundary value tests for V2 engine configuration parameters.
/// </summary>
public class SupportEngineV2BoundaryTests
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

    [Theory]
    [InlineData(0.01f)]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    public void DensityFactor_AllValues_NoError(float density)
    {
        var mesh = CreateCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { DensityFactor = density });
        result.Should().NotBeNull();
    }

    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.2f)]
    [InlineData(1.0f)]
    public void PinRadius_AllValues_NoError(float r)
    {
        var mesh = CreateCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { PinRadiusMm = r });
        result.Should().NotBeNull();
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    [InlineData(5.0f)]
    public void BaseRadius_AllValues_NoError(float r)
    {
        var mesh = CreateCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { BaseRadiusMm = r });
        result.Should().NotBeNull();
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.02f)]
    [InlineData(0.1f)]
    public void WideningFactor_AllValues_NoError(float w)
    {
        var mesh = CreateCube();
        var result = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { WideningFactor = w });
        result.Should().NotBeNull();
    }

    [Fact]
    public void MinSpacing_VerySmall_ProducesMore()
    {
        var mesh = CreateCube();
        var wide = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { MinSpacingMm = 10f, MaxSpacingMm = 15f });
        var tight = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { MinSpacingMm = 1f, MaxSpacingMm = 3f });

        tight.ValidSupports.Should().BeGreaterThanOrEqualTo(wide.ValidSupports);
    }

    [Fact]
    public void Seed_DifferentValues_MayDifferButNoError()
    {
        var mesh = CreateCube();
        var r1 = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { Seed = 1 });
        var r2 = SupportEngineV2.Generate(mesh, new SupportEngineV2.EngineConfig { Seed = 999 });

        r1.Should().NotBeNull();
        r2.Should().NotBeNull();
    }
}
