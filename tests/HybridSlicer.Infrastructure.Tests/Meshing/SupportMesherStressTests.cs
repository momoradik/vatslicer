using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

public class SupportMesherStressTests
{
    [Fact]
    public void Frustum_HighTessellation_ManyFaces()
    {
        var mesh = SupportMesher.Frustum(1f, 2f, 10f, 64);
        mesh.FaceCount.Should().BeGreaterThan(200);
    }

    [Fact]
    public void Sphere_HighTessellation_ManyFaces()
    {
        var mesh = SupportMesher.Sphere(5f, 16, 32);
        mesh.FaceCount.Should().BeGreaterThan(500);
    }

    [Fact]
    public void Generate100Frustums_Under500ms()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
            SupportMesher.Frustum(0.5f + i * 0.01f, 1f + i * 0.01f, 10f, 8);
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(500);
    }
}
