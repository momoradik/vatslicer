using System.Numerics;
using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Slicing;

namespace HybridSlicer.Infrastructure.Tests.Slicing;

public class AnalyticalSlicerEdgeCaseTests
{
    [Fact]
    public void SliceAtZ_TiltedElement_ExpandsRadius()
    {
        // A 45-degree tilted pillar — cross-section should be elliptical (larger radius)
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 10),
                PointB = new Vector3(10, 0, 0), // 45 degree tilt
                RadiusA = 1f, RadiusB = 1f,
                Type = "bridge",
            }
        };

        var circles = AnalyticalSupportSlicer.SliceAtZ(elements, 5f);

        circles.Should().HaveCount(1);
        // Tilted element should have effective radius > nominal radius
        circles[0].Radius.Should().BeGreaterThan(1f, "tilted element should have expanded radius");
    }

    [Fact]
    public void SliceAtZ_HorizontalElement_RendersAtMatchingZ()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 5),
                PointB = new Vector3(10, 0, 5), // horizontal
                RadiusA = 0.5f, RadiusB = 0.5f,
                Type = "interconnect",
            }
        };

        // At Z=5 (matching), should produce circles
        var atZ = AnalyticalSupportSlicer.SliceAtZ(elements, 5f);
        atZ.Should().NotBeEmpty("horizontal element should render at its Z");

        // At Z=4 or Z=6, should be empty
        AnalyticalSupportSlicer.SliceAtZ(elements, 4f).Should().BeEmpty();
        AnalyticalSupportSlicer.SliceAtZ(elements, 6f).Should().BeEmpty();
    }

    [Fact]
    public void SliceAtZ_ZeroRadiusElement_SkipsOrMinimal()
    {
        var elements = new List<AnalyticalSupportSlicer.SupportElement>
        {
            new()
            {
                PointA = new Vector3(0, 0, 10),
                PointB = new Vector3(0, 0, 0),
                RadiusA = 0f, RadiusB = 0f, // zero radius tip
                Type = "tip",
            }
        };

        var circles = AnalyticalSupportSlicer.SliceAtZ(elements, 5f);
        // May have 0 circles (skipped) or tiny circles — either is fine
        foreach (var c in circles)
            c.Radius.Should().BeLessThan(0.1f, "zero-radius element should produce tiny cross-section");
    }

    [Fact]
    public void SliceAll_EmptyElements_ReturnsEmpty()
    {
        var layers = AnalyticalSupportSlicer.SliceAll(new(), 1f, 0f, 10f);
        layers.Should().BeEmpty();
    }

    [Fact]
    public void ExtractElements_EmptyInputs_ReturnsEmpty()
    {
        var elements = AnalyticalSupportSlicer.ExtractElements(new(), new());
        elements.Should().BeEmpty();
    }

    [Fact]
    public void SliceAtZ_ManyElements_PerformsWell()
    {
        // 1000 vertical pillars
        var elements = new List<AnalyticalSupportSlicer.SupportElement>();
        for (int i = 0; i < 1000; i++)
        {
            elements.Add(new()
            {
                PointA = new Vector3(i * 2f, 0, 100),
                PointB = new Vector3(i * 2f, 0, 0),
                RadiusA = 0.5f, RadiusB = 0.5f,
                Type = "pillar",
            });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var circles = AnalyticalSupportSlicer.SliceAtZ(elements, 50f);
        sw.Stop();

        circles.Should().HaveCount(1000);
        sw.ElapsedMilliseconds.Should().BeLessThan(100, "1000 element slice should be fast");
    }
}
