namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Detailed print cost breakdown including resin, electricity, FEP wear,
/// and machine time. Reports per-item and per-hour costs.
/// </summary>
public static class PrintCostCalculator
{
    public sealed record CostBreakdown
    {
        public required float ResinCostUsd { get; init; }
        public required float ElectricityCostUsd { get; init; }
        public required float FepWearCostUsd { get; init; }
        public required float MachineTimeCostUsd { get; init; }
        public required float TotalCostUsd { get; init; }
        public required float CostPerMl { get; init; }
        public required float CostPerHour { get; init; }
    }

    public static CostBreakdown Calculate(
        float resinVolumeMl,
        float printTimeHours,
        float resinPricePerMl = 0.05f,
        float electricityKwhPrice = 0.12f,
        float printerWatts = 60f,
        float fepReplacementCostUsd = 15f,
        float fepLifeHours = 80f,
        float machineHourlyRateUsd = 0f)
    {
        float resinCost = resinVolumeMl * resinPricePerMl;
        float electricityCost = (printerWatts / 1000f) * printTimeHours * electricityKwhPrice;
        float fepCost = fepLifeHours > 0 ? (printTimeHours / fepLifeHours) * fepReplacementCostUsd : 0;
        float machineTimeCost = printTimeHours * machineHourlyRateUsd;
        float total = resinCost + electricityCost + fepCost + machineTimeCost;

        return new CostBreakdown
        {
            ResinCostUsd = resinCost,
            ElectricityCostUsd = electricityCost,
            FepWearCostUsd = fepCost,
            MachineTimeCostUsd = machineTimeCost,
            TotalCostUsd = total,
            CostPerMl = resinVolumeMl > 0 ? total / resinVolumeMl : 0,
            CostPerHour = printTimeHours > 0 ? total / printTimeHours : 0,
        };
    }
}
