namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Identifies layers where cross-section area changes abruptly.
/// These transition layers are where:
/// - Support attachment changes (islands start/end)
/// - Peel force spikes
/// - Layer adhesion is weakest (potential delamination)
/// </summary>
public static class LayerTransitionAnalyzer
{
    public sealed record Transition
    {
        public required int LayerIndex { get; init; }
        public required float ZMm { get; init; }
        public required float AreaChangePct { get; init; }
        public required string Type { get; init; } // "expansion", "contraction"
    }

    public sealed record TransitionReport
    {
        public required List<Transition> Transitions { get; init; }
        public required int TotalTransitions { get; init; }
        public required float MaxExpansionPct { get; init; }
        public required float MaxContractionPct { get; init; }
    }

    public static TransitionReport Analyze(
        CrossSectionAreaCalculator.AreaProfile profile,
        float thresholdPct = 30f)
    {
        var transitions = new List<Transition>();
        float maxExp = 0, maxCon = 0;

        for (int i = 1; i < profile.LayerCount; i++)
        {
            float prev = profile.AreasPerLayer[i - 1];
            float curr = profile.AreasPerLayer[i];
            if (prev < 0.1f && curr < 0.1f) continue;

            float baseline = Math.Max(prev, 0.1f);
            float changePct = (curr - prev) / baseline * 100f;

            if (Math.Abs(changePct) >= thresholdPct)
            {
                string type = changePct > 0 ? "expansion" : "contraction";
                transitions.Add(new Transition
                {
                    LayerIndex = i,
                    ZMm = profile.MeshMinZ + (i + 0.5f) * profile.LayerHeightMm,
                    AreaChangePct = changePct,
                    Type = type,
                });
                if (changePct > maxExp) maxExp = changePct;
                if (changePct < maxCon) maxCon = changePct;
            }
        }

        return new TransitionReport
        {
            Transitions = transitions,
            TotalTransitions = transitions.Count,
            MaxExpansionPct = maxExp,
            MaxContractionPct = maxCon,
        };
    }
}
