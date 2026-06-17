using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Meshing;

namespace HybridSlicer.Infrastructure.Tests.Meshing;

public class HollowedSupportTests
{
    [Fact]
    public void ShouldHollow_TallPillar_ReturnsTrue()
    {
        HollowedSupport.ShouldHollow(25f, 20f).Should().BeTrue(
            "25mm pillar exceeds 20mm threshold");
    }

    [Fact]
    public void ShouldHollow_ShortPillar_ReturnsFalse()
    {
        HollowedSupport.ShouldHollow(10f, 20f).Should().BeFalse(
            "10mm pillar is below 20mm threshold");
    }

    [Fact]
    public void HollowFrustum_ProducesValidMesh()
    {
        var mesh = HollowedSupport.HollowFrustum(
            rTopOuter: 1.0f,
            rBotOuter: 1.5f,
            wallThickness: 0.6f,
            height: 20f,
            sides: 12);

        mesh.VertexCount.Should().BeGreaterThan(0);
        mesh.FaceCount.Should().BeGreaterThan(0);
        mesh.FaceCount.Should().BeGreaterThan(20, "hollow frustum needs inner+outer walls");
    }

    [Fact]
    public void OrientedHollowFrustum_ProducesValidMesh()
    {
        var pointA = new Vector3(0, 0, 10);
        var pointB = new Vector3(0, 0, 30);
        var mesh = HollowedSupport.OrientedHollowFrustum(
            pointA, pointB,
            radiusA: 1.5f, radiusB: 1.0f,
            wallThickness: 0.6f, sides: 12);

        mesh.VertexCount.Should().BeGreaterThan(0);
        mesh.FaceCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void HollowFrustum_HasMoreFacesThanSolid()
    {
        var solid = SupportMesher.Frustum(1.0f, 1.5f, 20f, 12);
        var hollow = HollowedSupport.HollowFrustum(1.0f, 1.5f, 0.6f, 20f, 12);

        hollow.FaceCount.Should().BeGreaterThan(solid.FaceCount,
            "hollow mesh has inner walls → more faces");
    }
}
