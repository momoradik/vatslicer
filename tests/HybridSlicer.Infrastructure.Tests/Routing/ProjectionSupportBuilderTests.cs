using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Routing;

namespace HybridSlicer.Infrastructure.Tests.Routing;

public class ProjectionSupportBuilderTests
{
    [Fact]
    public void WithOverhangPoints_ProducesWalls()
    {
        var points = new List<(Vector3 position, Vector3 normal)>
        {
            (new Vector3(0, 0, 20), new Vector3(0, 0, -1)),
            (new Vector3(5, 0, 20), new Vector3(0, 0, -1)),
            (new Vector3(10, 0, 20), new Vector3(0, 0, -1)),
            (new Vector3(0, 5, 20), new Vector3(0, 0, -1)),
            (new Vector3(5, 5, 20), new Vector3(0, 0, -1)),
        };

        var result = ProjectionSupportBuilder.Build(points, 0f);

        result.WallCount.Should().BeGreaterThan(0);
        result.SliceElements.Should().NotBeEmpty();
        result.Mesh.FaceCount.Should().BeGreaterThan(0);
        result.VolumeMm3.Should().BeGreaterThan(0);
    }

    [Fact]
    public void EmptyPoints_NoOutput()
    {
        var result = ProjectionSupportBuilder.Build(
            Array.Empty<(Vector3, Vector3)>(), 0f);
        result.WallCount.Should().Be(0);
        result.SliceElements.Should().BeEmpty();
    }

    [Fact]
    public void HasBothWallsAndRafts()
    {
        var points = new List<(Vector3 position, Vector3 normal)>
        {
            (new Vector3(0, 0, 15), new Vector3(0, 0, -1)),
            (new Vector3(3, 3, 15), new Vector3(0, 0, -1)),
        };

        var result = ProjectionSupportBuilder.Build(points, 0f);

        result.SliceElements.Should().Contain(e => e.Type == "projection-wall");
        result.SliceElements.Should().Contain(e => e.Type == "raft");
    }

    [Fact]
    public void CustomConfig_RespectedSpacing()
    {
        var points = Enumerable.Range(0, 20).Select(i =>
            ((Vector3)new Vector3(i * 0.5f, 0, 10), (Vector3)new Vector3(0, 0, -1))
        ).ToList();

        var narrow = ProjectionSupportBuilder.Build(points, 0f,
            new ProjectionSupportBuilder.ProjectionConfig { HatchSpacingMm = 1f });
        var wide = ProjectionSupportBuilder.Build(points, 0f,
            new ProjectionSupportBuilder.ProjectionConfig { HatchSpacingMm = 5f });

        narrow.WallCount.Should().BeGreaterOrEqualTo(wide.WallCount,
            "narrower spacing → more walls");
    }
}
