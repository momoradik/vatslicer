namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Generates post-processing recommendations based on model properties
/// and print settings. Covers: washing, curing, support removal, sanding.
/// </summary>
public static class PostProcessingAdvisor
{
    public sealed record PostProcessStep
    {
        public required int Order { get; init; }
        public required string Name { get; init; }
        public required string Duration { get; init; }
        public required string Instructions { get; init; }
        public required bool Required { get; init; }
    }

    public sealed record PostProcessPlan
    {
        public required List<PostProcessStep> Steps { get; init; }
        public required float TotalTimeMinutes { get; init; }
        public required string ResinType { get; init; }
    }

    public static PostProcessPlan Generate(
        string resinType = "standard",
        bool hasSupports = true,
        bool isHollow = false,
        float surfaceAreaMm2 = 1000f)
    {
        var steps = new List<PostProcessStep>();
        float totalTime = 0;

        bool isWaterWashable = resinType.Contains("water", StringComparison.OrdinalIgnoreCase);

        // Step 1: Initial wash
        if (isWaterWashable)
        {
            steps.Add(new PostProcessStep { Order = 1, Name = "Water Rinse", Duration = "2-3 min",
                Instructions = "Rinse under warm running water. Agitate gently to remove uncured resin from surfaces.",
                Required = true });
            totalTime += 3;
        }
        else
        {
            steps.Add(new PostProcessStep { Order = 1, Name = "IPA Wash (Stage 1)", Duration = "3 min",
                Instructions = "Submerge in 90%+ IPA. Agitate or use ultrasonic cleaner. Replace IPA if cloudy.",
                Required = true });
            totalTime += 3;
        }

        // Step 2: Drain hollow (if applicable)
        if (isHollow)
        {
            steps.Add(new PostProcessStep { Order = 2, Name = "Drain Hollow Interior", Duration = "1-2 min",
                Instructions = "Shake and tilt to drain uncured resin through drain holes. Repeat wash cycle.",
                Required = true });
            totalTime += 2;
        }

        // Step 3: Second wash
        steps.Add(new PostProcessStep { Order = steps.Count + 1, Name = isWaterWashable ? "Water Wash (Stage 2)" : "IPA Wash (Stage 2)", Duration = "2 min",
            Instructions = "Second wash in clean solvent to remove residual uncured resin.",
            Required = true });
        totalTime += 2;

        // Step 4: Dry
        steps.Add(new PostProcessStep { Order = steps.Count + 1, Name = "Air Dry", Duration = "5-10 min",
            Instructions = "Let dry completely. Use compressed air for recesses and drain holes.",
            Required = true });
        totalTime += 8;

        // Step 5: Support removal
        if (hasSupports)
        {
            steps.Add(new PostProcessStep { Order = steps.Count + 1, Name = "Support Removal", Duration = "5-15 min",
                Instructions = "Remove supports with flush cutters. Work from largest to smallest. Support nubs can be sanded.",
                Required = true });
            totalTime += 10;
        }

        // Step 6: UV cure
        float cureTime = resinType.Contains("tough", StringComparison.OrdinalIgnoreCase) ? 10 : 5;
        steps.Add(new PostProcessStep { Order = steps.Count + 1, Name = "UV Post-Cure", Duration = $"{cureTime} min per side",
            Instructions = $"Cure under 405nm UV light for {cureTime} min. Rotate for even curing. Warm curing (60°C) improves properties.",
            Required = true });
        totalTime += cureTime * 2;

        // Step 7: Optional sanding
        steps.Add(new PostProcessStep { Order = steps.Count + 1, Name = "Sanding (Optional)", Duration = "10-30 min",
            Instructions = "Wet sand with 400→800→1200 grit for smooth finish. Focus on support marks and layer lines.",
            Required = false });

        return new PostProcessPlan
        {
            Steps = steps,
            TotalTimeMinutes = totalTime,
            ResinType = resinType,
        };
    }
}
