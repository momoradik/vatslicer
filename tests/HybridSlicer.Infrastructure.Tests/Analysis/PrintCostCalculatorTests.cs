using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class PrintCostCalculatorTests
{
    [Fact]
    public void BasicCost_ResinDominates()
    {
        var c = PrintCostCalculator.Calculate(10f, 2f);
        c.ResinCostUsd.Should().BeGreaterThan(c.ElectricityCostUsd);
        c.TotalCostUsd.Should().BeGreaterThan(0);
    }

    [Fact]
    public void AllComponents_Positive()
    {
        var c = PrintCostCalculator.Calculate(20f, 5f, machineHourlyRateUsd: 2f);
        c.ResinCostUsd.Should().BeGreaterThan(0);
        c.ElectricityCostUsd.Should().BeGreaterThan(0);
        c.FepWearCostUsd.Should().BeGreaterThan(0);
        c.MachineTimeCostUsd.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CostPerMl_Calculated()
    {
        var c = PrintCostCalculator.Calculate(10f, 1f);
        c.CostPerMl.Should().BeGreaterThan(0);
        c.CostPerHour.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TotalEquals_SumOfParts()
    {
        var c = PrintCostCalculator.Calculate(15f, 3f, machineHourlyRateUsd: 1f);
        float sum = c.ResinCostUsd + c.ElectricityCostUsd + c.FepWearCostUsd + c.MachineTimeCostUsd;
        c.TotalCostUsd.Should().BeApproximately(sum, 0.01f);
    }
}
